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
    /// <summary>
    /// The screenshot timeline. <paramref name="trigger"/> filters to PERIODIC or FLAGGED (null = both). By default
    /// only the lead frame of a clip is returned, so one flagged clip occupies a single timeline entry rather than
    /// dozens; pass <paramref name="includeClipFrames"/> to list every frame.
    /// </summary>
    Task<Paged<ScreenshotDto>> ListAsync(string deviceCode, DateTimeOffset? from, DateTimeOffset? to, string? trigger,
        bool includeClipFrames, PageRequest page, CancellationToken ct);

    Task<ScreenshotFile> GetImageAsync(string deviceCode, Guid id, CancellationToken ct);

    /// <summary>All frames of one flagged clip, in capture order. Viewing a clip is audited once.</summary>
    Task<ScreenshotClipDto> GetClipAsync(string deviceCode, Guid clipId, CancellationToken ct);
}

internal sealed class ScreenshotQueryService(IAppDbContext db, IScreenshotStore store, IAuditService audit) : IScreenshotQueryService
{
    public async Task<Paged<ScreenshotDto>> ListAsync(string deviceCode, DateTimeOffset? from, DateTimeOffset? to, string? trigger,
        bool includeClipFrames, PageRequest page, CancellationToken ct)
    {
        var device = await ResolveAsync(deviceCode, ct);
        var q = db.Screenshots.Where(s => s.DeviceId == device.Id);
        if (from is not null) q = q.Where(s => s.CapturedAtUtc >= from);
        if (to is not null) q = q.Where(s => s.CapturedAtUtc <= to);
        if (!string.IsNullOrWhiteSpace(trigger))
        {
            if (!ScreenTriggers.IsKnown(trigger)) throw new BadRequestException("trigger must be PERIODIC or FLAGGED.", nameof(trigger));
            q = q.Where(s => s.Trigger == trigger);
        }
        // Collapse clips to their first frame so a 20-frame burst is one timeline tile, not twenty.
        if (!includeClipFrames) q = q.Where(s => s.ClipId == null || s.ClipSeq == 0);
        q = q.OrderByDescending(s => s.CapturedAtUtc).ThenBy(s => s.ClipSeq);
        return await q.ToPagedAsync(page, Project, ct);
    }

    public async Task<ScreenshotClipDto> GetClipAsync(string deviceCode, Guid clipId, CancellationToken ct)
    {
        var device = await ResolveAsync(deviceCode, ct);
        var frames = await db.Screenshots.AsNoTracking()
            .Where(s => s.DeviceId == device.Id && s.ClipId == clipId)
            .OrderBy(s => s.ClipSeq)
            .ToListAsync(ct);
        if (frames.Count == 0) throw new NotFoundException("Clip not found.");

        var lead = frames[0];
        await audit.WriteAsync(AuditActions.ScreenshotViewed, device.DeviceCode,
            $"flagged clip '{lead.FlagTerm}' ({frames.Count} frames) @ {lead.CapturedAtUtc:u}", ct);
        return new ScreenshotClipDto(clipId, lead.CapturedAtUtc, lead.FlagTerm, lead.MatchedOn,
            frames.Count, lead.ClipTotal, frames.Select(Project).ToList());
    }

    private static ScreenshotDto Project(Screenshot s) =>
        new(s.Id, s.CapturedAtUtc, s.MonitorIndex, s.Width, s.Height, s.ByteSize, s.SessionUser, s.EmployeeId,
            s.Trigger, s.FlagTerm, s.MatchedOn, s.ClipId, s.ClipSeq, s.ClipTotal);

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
