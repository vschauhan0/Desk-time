namespace NISS.Domain.Entities;

/// <summary>One received heartbeat.</summary>
public sealed class Heartbeat
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>Server receive time.</summary>
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset DeviceTimestamp { get; set; }
    public string AgentVersion { get; set; } = "";
    public long UptimeSeconds { get; set; }
    public string ServiceState { get; set; } = "";
    public int QueueDepth { get; set; }
    public long ClockOffsetMs { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryUsedPercent { get; set; }
}

/// <summary>Live state of a device (one row per device): agent health plus what the user is doing right now.</summary>
public sealed class AgentStatus
{
    public Guid DeviceId { get; set; }
    public string ServiceState { get; set; } = "";
    public string Presence { get; set; } = "UNKNOWN";
    public string? UserName { get; set; }
    public int? WindowsSessionId { get; set; }
    public int? CurrentProcessId { get; set; }
    public string? CurrentApplication { get; set; }
    public string? CurrentExecutablePath { get; set; }
    public string? CurrentWindow { get; set; }
    public double IdleSeconds { get; set; }
    public bool Interacting { get; set; }
    public DateTimeOffset? LastInputAt { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryUsedPercent { get; set; }
    public double? DiskUsedPercent { get; set; }
    public bool? NetworkConnected { get; set; }
    /// <summary>JSON array of <c>OpenApplication</c>.</summary>
    public string? OpenApplicationsJson { get; set; }
    /// <summary>JSON array of <c>SessionAgentInfo</c>.</summary>
    public string? SessionAgentsJson { get; set; }
    /// <summary>JSON array of <c>CollectorStatus</c>.</summary>
    public string? CollectorsJson { get; set; }
    public int QueueDepth { get; set; }
    public int DeadLetterCount { get; set; }
    public DateTimeOffset? OldestQueuedAt { get; set; }
    public DateTimeOffset? LastUploadAt { get; set; }
    public string? LastUploadError { get; set; }
    public string? LastError { get; set; }
    public long ClockOffsetMs { get; set; }
    public int ConfigVersion { get; set; }
    public bool ServerReachable { get; set; } = true;
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    /// <summary>Time of the newest information folded into the live fields (heartbeat or event); older events are ignored.</summary>
    public DateTimeOffset LiveUpdatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Security / agent-health event, optionally resolved.</summary>
public sealed class SecurityEvent
{
    public Guid Id { get; set; }
    public Guid? DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public SecuritySeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>Every accepted event, whatever its type. Primary key <see cref="EventId"/> gives idempotency.</summary>
public sealed class EventIngestion : IDeviceOwned
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? SessionId { get; set; }
    public string EventType { get; set; } = "";
    /// <summary>Effective event time (device time corrected by clock offset, or receive time when it was in the future).</summary>
    public DateTimeOffset Timestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public long Sequence { get; set; }
    public string Source { get; set; } = "";
    public int SchemaVersion { get; set; }
    public long ClockOffsetMs { get; set; }
    public int LocalOffsetMinutes { get; set; }
    /// <summary>Denormalised subject (e.g. process name) so summaries do not need to parse the payload.</summary>
    public string? Subject { get; set; }
    /// <summary>Raw payload JSON.</summary>
    public string Payload { get; set; } = "{}";
}
