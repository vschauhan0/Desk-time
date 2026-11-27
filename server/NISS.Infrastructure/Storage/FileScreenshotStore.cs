using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Options;
using NISS.Application.Screenshots;
using NISS.Application.Settings;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Storage;

/// <summary>
/// Stores screenshots as JPEG files on disk with a metadata row in Postgres. The storage root is configurable at
/// runtime (Settings -&gt; Screenshots): a capture is written under the root in force when it is taken, and its path
/// relative to that root is saved on the row. Changing the root therefore affects new captures only - files already
/// written keep working as long as the root they were written under still resolves.
/// </summary>
internal sealed class FileScreenshotStore(
    IAppDbContext db,
    TimeProvider clock,
    ISettingsService settings,
    IOptions<ScreenshotStorageOptions> options) : IScreenshotStore
{
    // Last root resolved by a write, so the synchronous FullPath/OpenRead/Delete paths have a value without an async
    // settings read. Seeded with the static default so reads work before the first capture.
    private volatile string _root = DefaultRoot(options.Value.Root);

    private static string DefaultRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "niss-screenshots") : configured;

    private async Task<string> ResolveRootAsync(CancellationToken ct)
    {
        var configured = (await settings.GetScreenshotStorageAsync(ct)).Root;
        var root = DefaultRoot(string.IsNullOrWhiteSpace(configured) ? options.Value.Root : configured);
        _root = root;
        return root;
    }

    public string FullPath(Screenshot shot) => Path.Combine(_root, shot.RelPath);

    public async Task<Screenshot> SaveAsync(ScreenshotInput input, byte[] jpeg, CancellationToken ct)
    {
        var existing = await db.Screenshots
            .FirstOrDefaultAsync(s => s.DeviceId == input.DeviceId && s.FrameId == input.FrameId, ct);
        if (existing is not null) return existing;

        var root = await ResolveRootAsync(ct);

        var minFreeMb = options.Value.MinFreeDiskMb;
        if (minFreeMb > 0 && !ScreenshotDiskGuard.HasSpace(FreeBytes(root), minFreeMb))
            throw new AppException(507, "Insufficient storage", "Screenshot storage is low on disk; capture paused.");

        var id = Guid.NewGuid();
        var rel = BuildRelativePath(input, id);
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, jpeg, ct);

        var shot = new Screenshot
        {
            Id = id,
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
            Trigger = input.Trigger,
            FlagTerm = input.FlagTerm,
            MatchedOn = input.MatchedOn,
            ClipId = input.ClipId,
            ClipSeq = input.ClipSeq,
            ClipTotal = input.ClipTotal,
        };
        db.Screenshots.Add(shot);
        await db.SaveChangesAsync(ct);
        return shot;
    }

    /// <summary>
    /// Builds the on-disk path: <c>&lt;Employee&gt;/&lt;date&gt;/&lt;Employee&gt;_&lt;date&gt;_&lt;time&gt;[_monN][_clipNNNN].jpg</c>.
    /// The employee folder falls back to the device code when the capture is unattributed. A short id tail is added
    /// only when a file of that name already exists, so names stay readable but two captures never collide.
    /// </summary>
    private string BuildRelativePath(ScreenshotInput input, Guid id)
    {
        var who = Sanitize(!string.IsNullOrWhiteSpace(input.EmployeeName) ? input.EmployeeName! : input.DeviceCode);
        var local = input.CapturedAtUtc.ToLocalTime();
        var day = local.ToString("yyyy-MM-dd");
        var time = local.ToString("HH-mm-ss");

        var parts = $"{who}_{day}_{time}";
        if (input.Monitor >= 0) parts += $"_mon{input.Monitor}";
        if (input.ClipId is not null) parts += $"_clip{input.ClipSeq:D4}";

        var dir = Path.Combine(who, day);
        var rel = Path.Combine(dir, parts + ".jpg");
        // Collision guard: a flagged still can land in the same second as a periodic one on the same monitor. Keep the
        // readable name in the common case; append a short id tail only when that exact name is already taken.
        if (File.Exists(Path.Combine(_root, rel)))
            rel = Path.Combine(dir, $"{parts}_{id:N}"[..(parts.Length + 9)] + ".jpg");
        return rel;
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

    /// <summary>Makes one path segment safe: strips invalid characters and collapses whitespace to single underscores.</summary>
    private static string Sanitize(string s)
    {
        var cleaned = string.Concat((s ?? "").Trim().Split(Path.GetInvalidFileNameChars()));
        cleaned = string.Join("_", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    private static long FreeBytes(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root).AvailableFreeSpace; }
        catch { return long.MaxValue; } // can't determine (e.g. UNC): don't block capture
    }
}
