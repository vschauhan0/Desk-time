namespace NISS.Domain.Entities;

public static class UpdatePhase
{
    public const string Assigned = "Assigned";
    public const string Downloading = "Downloading";
    public const string Applying = "Applying";
    public const string Succeeded = "Succeeded";
    public const string RolledBack = "RolledBack";
    public const string Failed = "Failed";

    /// <summary>Phases in which an assigned update is still on its way.</summary>
    public static readonly string[] InProgress = [Assigned, Downloading, Applying];
}

/// <summary>Per-device update progress (one row per device).</summary>
public sealed class DeviceUpdateState
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string? AssignedVersion { get; set; }
    public string Phase { get; set; } = UpdatePhase.Assigned;
    public string? LastError { get; set; }
    /// <summary>
    /// Version that failed and was rolled back on this device. Quarantine: the device is never assigned this version again
    /// (not by a wave, not by the MinAgentVersion floor) until it is cleared on recovery.
    /// </summary>
    public string? FailedVersion { get; set; }
    public int Attempts { get; set; }
    /// <summary>Set by the SERVER when it serves this device its assigned package (never by the agent). Bounds the update-restart tamper suppression.</summary>
    public DateTimeOffset? PackageServedAtUtc { get; set; }
    /// <summary>The version whose first download stamped <see cref="PackageServedAtUtc"/> (server-written). A re-download of it does not refresh the window.</summary>
    public string? PackageServedVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
