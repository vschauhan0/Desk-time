using NISS.Domain.Entities;

namespace NISS.Application.Screenshots;

/// <summary>Disk-space guard for the screenshot store: refuse new captures before the volume can fill and wedge the DB.</summary>
public static class ScreenshotDiskGuard
{
    /// <summary>True when capture may proceed: the guard is off (<paramref name="minFreeMb"/> &lt;= 0) or free space is at/above it.</summary>
    public static bool HasSpace(long freeBytes, int minFreeMb) => minFreeMb <= 0 || freeBytes >= (long)minFreeMb * 1024 * 1024;
}

/// <summary>Everything needed to store one screenshot, independent of the agent pipe/transport types.</summary>
/// <param name="Trigger">One of <c>ScreenTriggers</c>: why the frame was captured.</param>
/// <param name="FlagTerm">The flagged term that matched, for a flagged capture.</param>
/// <param name="MatchedOn">Where the term matched (TITLE or PROCESS), for a flagged capture.</param>
/// <param name="ClipId">Groups the frames of one clip; null on a single still.</param>
/// <param name="ClipSeq">0-based position within the clip.</param>
/// <param name="ClipTotal">Frames the clip was expected to contain.</param>
public sealed record ScreenshotInput(
    Guid DeviceId,
    string DeviceCode,
    Guid? EmployeeId,
    string SessionUser,
    string FrameId,
    DateTimeOffset CapturedAtUtc,
    int Monitor,
    int Width,
    int Height,
    string Trigger = "PERIODIC",
    string? FlagTerm = null,
    string? MatchedOn = null,
    Guid? ClipId = null,
    int ClipSeq = 0,
    int ClipTotal = 0);

/// <summary>Stores periodic screenshots as files on disk plus a metadata row, and deletes them (retention/cleanup).</summary>
public interface IScreenshotStore
{
    /// <summary>Writes the JPEG to disk and inserts the metadata row. Idempotent on (DeviceId, FrameId).</summary>
    Task<Screenshot> SaveAsync(ScreenshotInput input, byte[] jpeg, CancellationToken ct);

    /// <summary>Absolute path of the stored JPEG for a row.</summary>
    string FullPath(Screenshot shot);

    /// <summary>Opens the stored JPEG for reading (streamed to the client).</summary>
    Stream OpenRead(Screenshot shot);

    /// <summary>Deletes the given rows and their files.</summary>
    Task DeleteAsync(IEnumerable<Screenshot> shots, CancellationToken ct);
}
