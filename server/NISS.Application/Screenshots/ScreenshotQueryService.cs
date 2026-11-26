using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Screenshots;

/// <summary>A stored screenshot's bytes plus the metadata the response needs.</summary>
public sealed record ScreenshotFile(Stream Content, DateTimeOffset CapturedAtUtc);

/// <summary>Reads stored screenshots for the dashboard timeline (SUPERADMIN). Viewing an image is audited.</summary>
public interface IScreenshotQueryService
{
    Task<Paged<ScreenshotDto>> ListAsync(string deviceCode, DateTimeOffset? from, DateTimeOffset? to, PageRequest page, CancellationToken ct);
    Task<ScreenshotFile> GetImageAsync(string deviceCode, Guid id, CancellationToken ct);
}

internal sealed class ScreenshotQueryService(IAppDbContext db, IScreenshotStore store, IAuditService audit) : IScreenshotQueryService
{
    public async Task<Paged<ScreenshotDto>> ListAsync(string deviceCode, DateTimeOffset? from, DateTimeOffset? to, PageRequest page, CancellationToken ct)
    {
        var device = await ResolveAsync(deviceCode, ct);
        var q = db.Screenshots.Where(s => s.DeviceId == device.Id);
        if (from is not null) q = q.Where(s => s.CapturedAtUtc >= from);
        if (to is not null) q = q.Where(s => s.CapturedAtUtc <= to);
        q = q.OrderByDescending(s => s.CapturedAtUtc);
        return await q.ToPagedAsync(page,
            s => new ScreenshotDto(s.Id, s.CapturedAtUtc, s.MonitorIndex, s.Width, s.Height, s.ByteSize, s.SessionUser, s.EmployeeId), ct);
    }

    public async Task<ScreenshotFile> GetImageAsync(string deviceCode, Guid id, CancellationToken ct)
    {
        var device = await ResolveAsync(deviceCode, ct);
        var shot = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == id && s.DeviceId == device.Id, ct)
            ?? throw new NotFoundException("Screenshot not found.");
        await audit.WriteAsync(AuditActions.ScreenshotViewed, device.DeviceCode,
            $"monitor {shot.MonitorIndex} @ {shot.CapturedAtUtc:u}", ct);
        return new ScreenshotFile(store.OpenRead(shot), shot.CapturedAtUtc);
    }

    private async Task<Device> ResolveAsync(string deviceCode, CancellationToken ct) =>
        await db.Devices.FirstOrDefaultAsync(d => d.DeviceCode == deviceCode, ct)
        ?? throw new NotFoundException("Device not found.");
}
