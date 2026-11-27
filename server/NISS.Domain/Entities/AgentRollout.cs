namespace NISS.Domain.Entities;

public static class RolloutState
{
    public const string Draft = "Draft";
    public const string Canary = "Canary";
    public const string RolledOut = "RolledOut";
    public const string Done = "Done";
    public const string Paused = "Paused";
    public const string RolledBack = "RolledBack";
}

/// <summary>
/// One staged rollout of an uploaded <see cref="AgentPackage"/> version: Draft, then a canary set (Canary), then everyone (RolledOut).
/// </summary>
public sealed class AgentRollout
{
    public Guid Id { get; set; }
    public string Version { get; set; } = "";
    public string State { get; set; } = RolloutState.Draft;
    /// <summary>Devices in the first wave. Only consulted while <see cref="State"/> is Canary.</summary>
    public Guid[] CanaryDeviceIds { get; set; } = [];
    /// <summary>How long the canary must run healthy before it may be promoted.</summary>
    public int BakeHours { get; set; } = 24;
    /// <summary>Fraction of canary devices that may fail before the rollout is rolled back (0..1).</summary>
    public double Threshold { get; set; } = 0.2;
    public string? Notes { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CanaryStartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
