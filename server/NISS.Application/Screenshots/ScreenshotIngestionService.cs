using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Monitor.Protocol;

namespace NISS.Application.Screenshots;

/// <summary>Metadata carried alongside an uploaded screenshot (bound from the request query).</summary>
/// <param name="Trigger">One of <see cref="ScreenTriggers"/>; anything unrecognised is stored as PERIODIC.</param>
/// <param name="FlagTerm">The flagged term that matched, for a flagged capture.</param>
/// <param name="MatchedOn">One of <see cref="FlagMatchKinds"/>, for a flagged capture.</param>
/// <param name="ClipId">Groups the frames of one clip.</param>
/// <param name="ClipSeq">0-based position within the clip.</param>
/// <param name="ClipTotal">Frames the clip was expected to contain.</param>
public sealed record ScreenshotMeta(
    string FrameId,
    int Monitor,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc,
    string? SessionUser,
    string? Trigger = null,
    string? FlagTerm = null,
    string? MatchedOn = null,
    Guid? ClipId = null,
    int ClipSeq = 0,
    int ClipTotal = 0);

public sealed record ScreenshotUploadResult(Guid Id, string Status);

public static class ScreenshotStatus
{
    public const string Stored = "STORED";
    public const string Duplicate = "DUPLICATE";
}

public interface IScreenshotIngestionService
{
    Task<ScreenshotUploadResult> IngestAsync(Guid deviceId, ScreenshotMeta meta, byte[] jpeg, CancellationToken ct);
}

internal sealed class ScreenshotIngestionService(IAppDbContext db, IScreenshotStore store) : IScreenshotIngestionService
{
    public async Task<ScreenshotUploadResult> IngestAsync(Guid deviceId, ScreenshotMeta meta, byte[] jpeg, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            ?? throw new NotFoundException("Device not found.");

        var existing = await db.Screenshots
            .FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.FrameId == meta.FrameId, ct);
        if (existing is not null) return new ScreenshotUploadResult(existing.Id, ScreenshotStatus.Duplicate);

        // Prefer per-session attribution (shared PCs) via the Windows username mapping, else the device's employee.
        Guid? employeeId = device.EmployeeId;
        if (!string.IsNullOrWhiteSpace(meta.SessionUser))
        {
            var mapped = await db.DeviceUsers.FirstOrDefaultAsync(
                u => u.DeviceId == deviceId && u.WindowsUsername == meta.SessionUser && u.EmployeeId != null, ct);
            if (mapped?.EmployeeId is not null) employeeId = mapped.EmployeeId;
        }
        // The employee's name drives the on-disk folder and file name (Settings -> Screenshots).
        var employeeName = employeeId is null ? null
            : await db.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.FullName).FirstOrDefaultAsync(ct);

        // An unknown trigger from a newer or tampered agent is normalised rather than trusted into the column.
        var trigger = ScreenTriggers.IsKnown(meta.Trigger) ? meta.Trigger! : ScreenTriggers.Periodic;
        var flagged = trigger == ScreenTriggers.Flagged;

        var shot = await store.SaveAsync(
            new ScreenshotInput(deviceId, device.DeviceCode, employeeId, meta.SessionUser ?? "", meta.FrameId,
                meta.CapturedAtUtc, meta.Monitor, meta.Width, meta.Height, trigger,
                flagged ? Trim(meta.FlagTerm, 128) : null,
                flagged ? Trim(meta.MatchedOn, 16) : null,
                flagged ? meta.ClipId : null,
                flagged ? Math.Max(0, meta.ClipSeq) : 0,
                flagged ? Math.Max(0, meta.ClipTotal) : 0,
                employeeName),
            jpeg, ct);
        return new ScreenshotUploadResult(shot.Id, ScreenshotStatus.Stored);
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
