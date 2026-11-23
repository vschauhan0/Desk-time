namespace NISS.Monitor.Protocol;

// ---------------------------------------------------------------------------------------------
// Endpoint <-> server contract. Device authentication header (all endpoints except enroll/health):
//     Authorization: NissDevice <deviceId>:<deviceSecret>
// ---------------------------------------------------------------------------------------------

/// <summary>POST /api/v1/devices/enroll (anonymous; authenticated by the one-time <see cref="EnrollmentToken"/>).</summary>
public sealed record EnrollRequest(
    string EnrollmentToken,
    string Hostname,
    string? MachineGuid,
    string? MachineSerial,
    string? EmployeeId,
    string WindowsUsername,
    string AgentVersion,
    string? OsVersion,
    string? Manufacturer,
    string? Model);

/// <summary>Enrollment result. <see cref="DeviceSecret"/> is returned exactly once and never stored in clear by the server.</summary>
public sealed record EnrollResponse(
    string DeviceId,
    string DeviceSecret,
    string? EmployeeId,
    DateTimeOffset ServerTimeUtc,
    AgentPolicy Policy);

/// <summary>POST /api/v1/devices/credentials/rotate - device-authenticated; returns a new secret and invalidates the old one.</summary>
public sealed record RotateCredentialsResponse(string DeviceSecret, DateTimeOffset RotatedAtUtc);

/// <summary>POST /api/v1/events/batch (also accepts SYSTEM_METRIC events on /api/v1/metrics/batch).</summary>
public sealed record EventBatchRequest(string DeviceId, IReadOnlyList<EventEnvelope> Events);

/// <summary>Per-event outcome so the agent can prune its queue precisely.</summary>
/// <param name="Status">ACCEPTED, DUPLICATE (already stored - safe to drop) or REJECTED (permanent - dead-letter it).</param>
public sealed record EventResult(Guid EventId, string Status, string? Error);

/// <summary>Batch ingest response.</summary>
public sealed record EventBatchResponse(
    int Accepted,
    int Duplicates,
    int Rejected,
    IReadOnlyList<EventResult> Results,
    DateTimeOffset ServerTimeUtc);

/// <summary>Constant values for <see cref="EventResult.Status"/>.</summary>
public static class EventResultStatus
{
    public const string Accepted = "ACCEPTED";
    public const string Duplicate = "DUPLICATE";
    public const string Rejected = "REJECTED";
}

/// <summary>State of a session-agent process as seen by the service.</summary>
public sealed record SessionAgentInfo(int WindowsSessionId, string? UserName, bool Connected, DateTimeOffset? LastSeenUtc);

/// <summary>A window-owning application currently open on the device.</summary>
public sealed record OpenApplication(int ProcessId, string ProcessName, string? ExecutablePath, string? WindowTitle, DateTimeOffset? StartedUtc);

/// <summary>Foreground application snapshot.</summary>
public sealed record ForegroundInfo(int ProcessId, string ProcessName, string? ExecutablePath, string? WindowTitle);

/// <summary>Live state carried in heartbeats. Keeps "running", "foreground" and "interaction" separate.</summary>
/// <param name="Presence">One of <see cref="Presence"/>.</param>
/// <param name="IdleSeconds">Seconds since last input (0 when unknown).</param>
/// <param name="Interacting">True when input was seen within the interaction window (default 30 s).</param>
public sealed record LiveState(
    string Presence,
    string? UserName,
    int? WindowsSessionId,
    ForegroundInfo? Foreground,
    IReadOnlyList<OpenApplication> OpenApplications,
    double IdleSeconds,
    bool Interacting,
    DateTimeOffset? LastInputUtc,
    double? CpuPercent,
    double? MemoryUsedPercent,
    bool? NetworkConnected);

/// <summary>POST /api/v1/devices/heartbeat (alias: /api/v1/heartbeat).</summary>
public sealed record HeartbeatRequest(
    string DeviceId,
    string AgentVersion,
    DateTimeOffset TimestampUtc,
    long UptimeSeconds,
    string ServiceState,
    IReadOnlyList<SessionAgentInfo> SessionAgents,
    int QueueDepth,
    DateTimeOffset? OldestQueuedUtc,
    DateTimeOffset? LastUploadUtc,
    string? LastError,
    long ClockOffsetMs,
    int ConfigVersion,
    LiveState? Live);

/// <summary>Heartbeat response. <see cref="ServerTimeUtc"/> is used by the agent to measure clock drift.</summary>
/// <param name="DeviceStatus">ACTIVE, DISABLED or REVOKED. The agent stops collecting/uploading unless ACTIVE.</param>
/// <param name="ConfigVersion">If greater than the agent's version it should GET /api/v1/agent/config.</param>
public sealed record HeartbeatResponse(
    DateTimeOffset ServerTimeUtc,
    string DeviceStatus,
    int ConfigVersion,
    string? MinAgentVersion,
    string? LatestAgentVersion);

/// <summary>State of one collector inside the agent.</summary>
public sealed record CollectorStatus(string Name, string State, string? LastError, DateTimeOffset? LastEventUtc);

/// <summary>POST /api/v1/agent/status - richer than the heartbeat, sent every few minutes and on change.</summary>
public sealed record AgentStatusRequest(
    string DeviceId,
    string AgentVersion,
    IReadOnlyList<CollectorStatus> Collectors,
    int QueueDepth,
    int DeadLetterCount,
    DateTimeOffset? OldestQueuedUtc,
    DateTimeOffset? LastUploadUtc,
    string? LastUploadError,
    string? LastError,
    bool ServerReachable,
    IReadOnlyList<SessionAgentInfo> SessionAgents,
    string? OsVersion,
    string? Hostname,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    double? CpuPercent,
    double? MemoryUsedPercent,
    double? DiskUsedPercent);

/// <summary>GET /api/v1/agent/config.</summary>
public sealed record AgentConfigResponse(
    string DeviceId,
    string? EmployeeId,
    DateTimeOffset ServerTimeUtc,
    AgentPolicy Policy);

/// <summary>File-activity module settings. Disabled by default.</summary>
public sealed record FileMonitoringPolicy(bool Enabled, IReadOnlyList<string> Paths, IReadOnlyList<string> IgnoreExtensions);

/// <summary>Optional cursor sampling. Disabled by default; never a raw trajectory.</summary>
public sealed record CursorSamplingPolicy(bool Enabled, int IntervalSeconds);

/// <summary>Effective monitoring policy pushed to an agent. Every value is server-configurable.</summary>
public sealed record AgentPolicy
{
    public int Version { get; init; } = 1;
    public int HeartbeatSeconds { get; init; } = 30;
    public int MetricsSeconds { get; init; } = 30;
    public int ForegroundPollMilliseconds { get; init; } = 1000;
    public int IdleThresholdMinutes { get; init; } = 5;
    public int InteractionWindowSeconds { get; init; } = 30;
    public int InputBucketSeconds { get; init; } = 30;
    public bool CaptureWindowTitles { get; init; } = true;
    public bool CaptureCommandLines { get; init; }
    public bool ComputeProcessHashes { get; init; }
    /// <summary>"Interactive" (user-session processes only, noise excluded) or "All".</summary>
    public string ProcessMonitoringMode { get; init; } = "Interactive";
    public IReadOnlyList<string> ProcessExclusions { get; init; } = [];
    public FileMonitoringPolicy FileMonitoring { get; init; } = new(false, [], []);
    public CursorSamplingPolicy CursorSampling { get; init; } = new(false, 5);
    public bool UsbMonitoringEnabled { get; init; } = true;
    public int UploadBatchSize { get; init; } = 200;
    public int UploadIntervalSeconds { get; init; } = 5;
    public int BacklogWarningThreshold { get; init; } = 5000;
    public string? MinAgentVersion { get; init; }
    public string? LatestAgentVersion { get; init; }
}
