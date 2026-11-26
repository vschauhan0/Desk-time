using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Options;
using NISS.Application.Screenshots;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Storage;

/// <summary>Stores screenshots as plaintext JPEG files on disk with a metadata row in Postgres.</summary>
internal sealed class FileScreenshotStore(
    IAppDbContext db,
    TimeProvider clock,
    IOptions<ScreenshotStorageOptions> options) : IScreenshotStore
{
    private string Root => string.IsNullOrWhiteSpace(options.Value.Root)
        ? Path.Combine(Path.GetTempPath(), "niss-screenshots")
        : options.Value.Root;

    public string FullPath(Screenshot shot) => Path.Combine(Root, shot.RelPath);

    public async Task<Screenshot> SaveAsync(ScreenshotInput input, byte[] jpeg, CancellationToken ct)
    {
        var existing = await db.Screenshots
            .FirstOrDefaultAsync(s => s.DeviceId == input.DeviceId && s.FrameId == input.FrameId, ct);
        if (existing is not null) return existing;

        var minFreeMb = options.Value.MinFreeDiskMb;
        if (minFreeMb > 0 && !ScreenshotDiskGuard.HasSpace(FreeBytes(Root), minFreeMb))
            throw new AppException(507, "Insufficient storage", "Screenshot storage is low on disk; capture paused.");

        var day = input.CapturedAtUtc.UtcDateTime.ToString("yyyy-MM-dd");
        var rel = Path.Combine(Sanitize(input.DeviceCode), day, $"{input.CapturedAtUtc.UtcTicks}-{input.Monitor}.jpg");
        var full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, jpeg, ct);

        var shot = new Screenshot
        {
            Id = Guid.NewGuid(),
            DeviceId = input.DeviceId,
            EmployeeId = input.EmployeeId,
            SessionUser = input.SessionUser,
            CapturedAtUtc = input.CapturedAtUtc,
            MonitorIndex = input.Monitor,
            Width = input.Width,
            Height = input.Height,
            ByteSize = jpeg.Length,
            RelPath = rel,
            FrameId = input.FrameId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Screenshots.Add(shot);
        await db.SaveChangesAsync(ct);
        return shot;
    }

    public Stream OpenRead(Screenshot shot) => File.OpenRead(FullPath(shot));

    public async Task DeleteAsync(IEnumerable<Screenshot> shots, CancellationToken ct)
    {
        var list = shots.ToList();
        if (list.Count == 0) return;
        foreach (var s in list)
        {
            try
            {
                var f = FullPath(s);
                if (File.Exists(f)) File.Delete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        db.Screenshots.RemoveRange(list);
        await db.SaveChangesAsync(ct);
    }

    private static string Sanitize(string s)
    {
        var cleaned = string.Concat(s.Split(Path.GetInvalidFileNameChars()));
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    private static long FreeBytes(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root).AvailableFreeSpace; }
        catch { return long.MaxValue; } // can't determine (e.g. UNC): don't block capture
    }
}
