using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Live;
using NISS.Application.Policies;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Devices;

/// <summary>Device enrollment and enrollment-token administration.</summary>
public interface IEnrollmentService
{
    /// <summary>Consumes a one-time token, creates or re-enrolls the device and returns a fresh secret (shown once).</summary>
    Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct);

    Task<EnrollmentTokenDto> CreateTokenAsync(EnrollmentTokenRequest request, CancellationToken ct);
    Task<IReadOnlyList<EnrollmentTokenDto>> ListTokensAsync(CancellationToken ct);
    Task DeleteTokenAsync(Guid id, CancellationToken ct);
}

internal sealed class EnrollmentService(
    IAppDbContext db, ISecretHasher hasher, IDeviceNumberSequence sequence, IPolicyService policies, TimeProvider clock,
    IAuditService audit, ICurrentUser user, ILiveStateService live, ILogger<EnrollmentService> log) : IEnrollmentService
{
    private const string TokenPrefix = "enr_";

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = hasher.Hash(request.EnrollmentToken.Trim());

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Atomic single-use consumption: only one caller can flip used_at from null.
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
        {
            log.LogWarning("Enrollment refused: token unknown, used or expired (host {Hostname})", request.Hostname);
            throw new UnauthorizedAppException("The enrollment token is invalid, expired or already used.");
        }
        var consumed = await db.EnrollmentTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (consumed != 1) throw new UnauthorizedAppException("The enrollment token is invalid, expired or already used.");

        var machineGuid = string.IsNullOrWhiteSpace(request.MachineGuid) ? null : request.MachineGuid.Trim();
        Device? device = null;
        var reEnroll = false;
        if (token.BoundDeviceId is { } bound)
        {
            device = await db.Devices.FirstOrDefaultAsync(d => d.Id == bound, ct) ?? throw new ConflictException("The device this token was issued for no longer exists.");
            reEnroll = true;
        }
        else if (machineGuid is not null)
        {
            device = await db.Devices.FirstOrDefaultAsync(d => d.MachineGuid == machineGuid, ct);
            if (device is not null)
            {
                if (device.Status is DeviceHealth.Revoked or DeviceHealth.Disabled)
                    throw new ForbiddenException($"Device {device.DeviceCode} is {device.Status}; an administrator must issue a re-enroll token for it.");
                reEnroll = true;
            }
        }

        var employee = await ResolveEmployeeAsync(token.EmployeeCode ?? request.EmployeeId, now, ct);

        if (device is null)
        {
            var n = await sequence.NextAsync(ct);
            device = new Device { Id = Guid.NewGuid(), DeviceCode = $"PC-{n:000}", CreatedAt = now, Status = DeviceHealth.Pending };
            db.Devices.Add(device);
            db.AgentStatuses.Add(new AgentStatus { DeviceId = device.Id, LiveUpdatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = now });
        }

        var secret = hasher.GenerateSecret();
        device.Hostname = Text.Trunc(request.Hostname.Trim(), 255)!;
        device.MachineGuid = machineGuid ?? device.MachineGuid;
        device.MachineSerial = Text.Trunc(request.MachineSerial, 128) ?? device.MachineSerial;
        device.Manufacturer = Text.Trunc(request.Manufacturer, 128) ?? device.Manufacturer;
        device.Model = Text.Trunc(request.Model, 128) ?? device.Model;
        device.Os = Text.Trunc(request.OsVersion, 256) ?? device.Os;
        device.OsVersion = Text.Trunc(request.OsVersion, 256) ?? device.OsVersion;
        device.AgentVersion = Text.Trunc(request.AgentVersion, 64) ?? "";
        device.EmployeeId = employee?.Id ?? device.EmployeeId;
        device.SecretHash = hasher.Hash(secret);
        device.SecretRotatedAt = now;
        device.ServiceStoppedAt = null;
        device.Status = DeviceHealth.Pending;
        device.LastSeen = null;
        device.UpdatedAt = now;
        token.UsedByDeviceId = device.Id;

        var windowsUser = Text.Trunc(request.WindowsUsername?.Trim(), 256);
        if (!string.IsNullOrEmpty(windowsUser))
        {
            var existing = await db.DeviceUsers.FirstOrDefaultAsync(u => u.DeviceId == device.Id && u.WindowsUsername.ToLower() == windowsUser.ToLower(), ct);
            if (existing is null)
                db.DeviceUsers.Add(new DeviceUser { Id = Guid.NewGuid(), DeviceId = device.Id, WindowsUsername = windowsUser, EmployeeId = device.EmployeeId, FirstSeen = now, LastSeen = now });
            else { existing.LastSeen = now; existing.EmployeeId ??= device.EmployeeId; }
        }

        audit.Record(AuditActions.DeviceEnrolled, device.DeviceCode,
            $"{(reEnroll ? "re-enrolled" : "enrolled")} host {device.Hostname}; employee {employee?.EmployeeCode ?? "-"}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await live.PublishAsync(device.Id, ct);
        var policy = await policies.GetEffectiveAsync(device.DeviceCode, employee?.Department, ct);
        log.LogInformation("Device {DeviceCode} {Action}", device.DeviceCode, reEnroll ? "re-enrolled" : "enrolled");
        return new EnrollResponse(device.DeviceCode, secret, employee?.EmployeeCode, now, policy);
    }

    private async Task<Employee?> ResolveEmployeeAsync(string? code, DateTimeOffset now, CancellationToken ct)
    {
        code = code?.Trim();
        if (string.IsNullOrEmpty(code)) return null;
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeCode.ToLower() == code.ToLower(), ct);
        if (employee is not null) return employee;
        employee = new Employee { Id = Guid.NewGuid(), EmployeeCode = code, FullName = code, Active = true, CreatedAt = now, UpdatedAt = now };
        db.Employees.Add(employee);   // placeholder, an admin can complete the profile later
        return employee;
    }

    public async Task<EnrollmentTokenDto> CreateTokenAsync(EnrollmentTokenRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        Guid? deviceId = null;
        string? deviceCode = null;
        if (!string.IsNullOrWhiteSpace(request.DeviceId))
        {
            var code = request.DeviceId.Trim();
            var d = await db.Devices.AsNoTracking().Where(x => x.DeviceCode == code).Select(x => new { x.Id, x.DeviceCode }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException($"Device {code} not found.");
            deviceId = d.Id; deviceCode = d.DeviceCode;
        }

        var raw = TokenPrefix + hasher.GenerateSecret();
        var row = new EnrollmentToken
        {
            Id = Guid.NewGuid(), TokenHash = hasher.Hash(raw), EmployeeCode = string.IsNullOrWhiteSpace(request.EmployeeCode) ? null : request.EmployeeCode.Trim(),
            BoundDeviceId = deviceId, ExpiresAt = now.AddHours(request.ExpiresInHours), Note = Text.Trunc(request.Note, 500),
            CreatedBy = user.Username, CreatedAt = now,
        };
        db.EnrollmentTokens.Add(row);
        audit.Record(AuditActions.EnrollmentTokenCreated, deviceCode ?? row.EmployeeCode, $"expires {row.ExpiresAt:u}");
        await db.SaveChangesAsync(ct);
        return ToDto(row, raw, deviceCode, null);
    }

    public async Task<IReadOnlyList<EnrollmentTokenDto>> ListTokensAsync(CancellationToken ct)
    {
        var rows = await db.EnrollmentTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ct);
        var ids = rows.SelectMany(r => new[] { r.BoundDeviceId, r.UsedByDeviceId }).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var codes = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DeviceCode, ct);
        string? Code(Guid? id) => id is { } g && codes.TryGetValue(g, out var c) ? c : null;
        return rows.Select(r => ToDto(r, null, Code(r.BoundDeviceId), Code(r.UsedByDeviceId))).ToList();
    }

    public async Task DeleteTokenAsync(Guid id, CancellationToken ct)
    {
        var row = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Enrollment token not found.");
        db.EnrollmentTokens.Remove(row);
        audit.Record(AuditActions.EnrollmentTokenDeleted, id.ToString(), row.EmployeeCode);
        await db.SaveChangesAsync(ct);
    }

    internal static EnrollmentTokenDto ToDto(EnrollmentToken t, string? rawToken, string? boundDeviceCode, string? usedByCode) =>
        new(t.Id, rawToken, t.EmployeeCode, boundDeviceCode, t.ExpiresAt, t.UsedAt, usedByCode, t.Note, t.CreatedAt);
}
