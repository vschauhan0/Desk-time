using NISS.Domain.Entities;

namespace NISS.Application.Screenshots;

/// <summary>Disk-space guard for the screenshot store: refuse new captures before the volume can fill and wedge the DB.</summary>
public static class ScreenshotDiskGuard
{
    /// <summary>True when capture may proceed: the guard is off (<paramref name="minFreeMb"/> &lt;= 0) or free space is at/above it.</summary>
    public static bool HasSpace(long freeBytes, int minFreeMb) => minFreeMb <= 0 || freeBytes >= (long)minFreeMb * 1024 * 1024;
}

/// <summary>Everything needed to store one screenshot, independent of the agent pipe/transport types.</summary>
public sealed record ScreenshotInput(
    Guid DeviceId,
    string DeviceCode,
    Guid? EmployeeId,
    string SessionUser,
    string FrameId,
    DateTimeOffset CapturedAtUtc,
    int Monitor,
    int Width,
    int Height);

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
