using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Live;
using NISS.Application.Settings;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Devices;

/// <summary>Result of authenticating a device request.</summary>
public sealed record DeviceAuthResult(bool Success, Guid DeviceId, string DeviceCode, string Status, string? Failure)
{
    public static DeviceAuthResult Fail(string reason) => new(false, Guid.Empty, "", "", reason);
}

/// <summary>Verifies <c>Authorization: NissDevice &lt;deviceId&gt;:&lt;secret&gt;</c> credentials.</summary>
public interface IDeviceAuthenticator
{
    Task<DeviceAuthResult> AuthenticateAsync(string deviceCode, string secret, CancellationToken ct);
}

internal sealed class DeviceAuthenticator(
    IAppDbContext db, ISecretHasher hasher, ISecurityEventService security, INotificationBuffer buffer,
    Microsoft.Extensions.Logging.ILogger<DeviceAuthenticator> log) : IDeviceAuthenticator
{
    private static readonly byte[] DummyHash = new byte[32];

    public async Task<DeviceAuthResult> AuthenticateAsync(string deviceCode, string secret, CancellationToken ct)
    {
        var code = deviceCode.Trim();
        var device = await db.Devices.AsNoTracking().Where(d => d.DeviceCode == code)
            .Select(d => new { d.Id, d.DeviceCode, d.Status, d.SecretHash }).FirstOrDefaultAsync(ct);

        var ok = hasher.Verify(secret, device?.SecretHash ?? DummyHash);   // always hash: no timing difference for unknown devices
        if (device is not null && device.SecretHash is not null && ok)
            return new DeviceAuthResult(true, device.Id, device.DeviceCode, device.Status, null);

        // The id comes from an unauthenticated caller: cap its length and drop control characters before it reaches a log line.
        var shown = new string(code.Take(32).Select(c => char.IsControl(c) ? '_' : c).ToArray());
        log.LogWarning("Device authentication failed for {DeviceCode}", shown);
        if (device is not null)
        {
            var raised = await security.RaiseAsync(new DeviceRef(device.Id, device.DeviceCode), SecurityEventKinds.AuthFailure, SecuritySeverity.Warning,
                $"{device.DeviceCode}: authentication with an invalid or rotated secret was refused.", onlyIfNoneOpen: false, throttle: TimeSpan.FromMinutes(10), ct: ct);
            if (raised is not null) { await db.SaveChangesAsync(ct); await buffer.FlushAsync(ct); }
        }
        return DeviceAuthResult.Fail("invalid device credentials");
    }
}

/// <summary>Administrative actions on a device (all audited).</summary>
public interface IDeviceCommandService
{
    Task DisableAsync(string deviceCode, CancellationToken ct);
    Task EnableAsync(string deviceCode, CancellationToken ct);
    Task RevokeAsync(string deviceCode, CancellationToken ct);
    /// <summary>Creates a token bound to the device (returned in clear once) that lets the agent enroll again.</summary>
    Task<EnrollmentTokenDto> ReEnrollAsync(string deviceCode, int expiresInHours, CancellationToken ct);
    /// <summary>Device-authenticated rotation: new secret, the old one stops working immediately.</summary>
    Task<RotateCredentialsResponse> RotateSecretAsync(Guid deviceId, CancellationToken ct);
}

internal sealed class DeviceCommandService(
    IAppDbContext db, TimeProvider clock, IAuditService audit, IDeviceStatusService status, ISettingsService settings,
    ISecurityEventService security, INotificationBuffer buffer, ILiveStateService live, IEnrollmentService enrollment, ISecretHasher hasher)
    : IDeviceCommandService
{
    private async Task<Device> FindAsync(string code, CancellationToken ct)
    {
        var c = code.Trim();
        return await db.Devices.FirstOrDefaultAsync(d => d.DeviceCode == c, ct) ?? throw new NotFoundException($"Device {c} not found.");
    }

    public async Task DisableAsync(string deviceCode, CancellationToken ct)
    {
        var d = await FindAsync(deviceCode, ct);
        if (d.Status == DeviceHealth.Revoked) throw new ConflictException("A revoked device cannot be disabled; re-enroll it instead.");
        await status.ApplyAsync(d, DeviceHealth.Disabled, "disabled by administrator", ct);
        audit.Record(AuditActions.DeviceDisabled, d.DeviceCode);
        await Finish(d, ct);
    }

    public async Task EnableAsync(string deviceCode, CancellationToken ct)
    {
        var d = await FindAsync(deviceCode, ct);
        if (d.Status != DeviceHealth.Disabled) throw new ConflictException("Only a disabled device can be enabled.");
        var thresholds = await settings.GetThresholdsAsync(ct);
        var next = HealthEvaluator.Evaluate(DeviceHealth.Pending, d.LastSeen, d.ServiceStoppedAt, clock.GetUtcNow(), thresholds);
        await status.ApplyAsync(d, next, "enabled by administrator", ct);
        audit.Record(AuditActions.DeviceEnabled, d.DeviceCode);
        await Finish(d, ct);
    }

    public async Task RevokeAsync(string deviceCode, CancellationToken ct)
    {
        var d = await FindAsync(deviceCode, ct);
        await status.ApplyAsync(d, DeviceHealth.Revoked, "revoked by administrator", ct);
        await security.RaiseAsync(new DeviceRef(d.Id, d.DeviceCode), SecurityEventKinds.DeviceRevoked, SecuritySeverity.Critical,
            $"{d.DeviceCode} ({d.Hostname}) was revoked by an administrator.", onlyIfNoneOpen: false, ct: ct);
        audit.Record(AuditActions.DeviceRevoked, d.DeviceCode);
        await Finish(d, ct);
    }

    public async Task<EnrollmentTokenDto> ReEnrollAsync(string deviceCode, int expiresInHours, CancellationToken ct)
    {
        var d = await FindAsync(deviceCode, ct);
        var token = await enrollment.CreateTokenAsync(new EnrollmentTokenRequest(null, d.DeviceCode, expiresInHours <= 0 ? 24 : expiresInHours, "re-enroll"), ct);
        await audit.WriteAsync(AuditActions.DeviceReEnrollRequested, d.DeviceCode, null, ct);
        return token;
    }

    public async Task<RotateCredentialsResponse> RotateSecretAsync(Guid deviceId, CancellationToken ct)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (d.Status is DeviceHealth.Disabled or DeviceHealth.Revoked) throw new ForbiddenException($"Device is {d.Status}.");
        var now = clock.GetUtcNow();
        var secret = hasher.GenerateSecret();
        d.SecretHash = hasher.Hash(secret);
        d.SecretRotatedAt = now;
        audit.Record(AuditActions.DeviceSecretRotated, d.DeviceCode);
        await db.SaveChangesAsync(ct);
        return new RotateCredentialsResponse(secret, now);
    }

    private async Task Finish(Device d, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await buffer.FlushAsync(ct);
        await live.PublishAsync(d.Id, ct);
    }
}
