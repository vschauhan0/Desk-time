using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;

namespace NISS.Application.Screenshots;

/// <summary>Metadata carried alongside an uploaded periodic screenshot (bound from the request query).</summary>
public sealed record ScreenshotMeta(
    string FrameId,
    int Monitor,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc,
    string? SessionUser);

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

        var shot = await store.SaveAsync(
            new ScreenshotInput(deviceId, device.DeviceCode, employeeId, meta.SessionUser ?? "", meta.FrameId,
                meta.CapturedAtUtc, meta.Monitor, meta.Width, meta.Height),
            jpeg, ct);
        return new ScreenshotUploadResult(shot.Id, ScreenshotStatus.Stored);
    }
}
