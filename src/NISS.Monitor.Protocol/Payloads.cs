namespace NISS.Monitor.Protocol;

// ---------------------------------------------------------------------------------------------
// Event payloads. One record per event family. See docs/monitoring-events.md for the mapping.
// None of these types can carry typed characters, clipboard data or file contents by design.
// ---------------------------------------------------------------------------------------------

/// <summary>PROCESS_STARTED / PROCESS_STOPPED.</summary>
/// <param name="ProcessSessionId">Deterministic id for one process lifetime (device + pid + start time).</param>
/// <param name="Baseline">True when the process was already running when the agent started (start time is the real one).</param>
public sealed record ProcessPayload(
    Guid ProcessSessionId,
    int ProcessId,
    int? ParentProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? UserName,
    int WindowsSessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string? CommandLine,
    string? Sha256,
    bool Baseline);

/// <summary>APPLICATION_STARTED / APPLICATION_STOPPED - a process that owns a visible top-level window opened / closed.</summary>
public sealed record ApplicationLifecyclePayload(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? WindowTitle,
    int WindowsSessionId,
    DateTimeOffset? OpenedUtc,
    DateTimeOffset? ClosedUtc);

/// <summary>APPLICATION_FOREGROUND - an application became the foreground window owner.</summary>
public sealed record ApplicationForegroundPayload(
    Guid AppSessionId,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? WindowTitle,
    string? PreviousProcessName,
    int WindowsSessionId);

/// <summary>
/// APPLICATION_BACKGROUND - the foreground session ended. This is the closed application session
/// (the server upserts application_sessions by <see cref="AppSessionId"/>).
/// </summary>
/// <param name="DurationSeconds">Time the app owned the foreground (StartedUtc..EndedUtc).</param>
/// <param name="InteractionSeconds">Portion of that time with recent mouse/keyboard input ("actively used").</param>
/// <param name="IdleSeconds">Portion of that time the user was idle past the idle threshold.</param>
/// <param name="EndReason">One of <see cref="AppSessionEndReasons"/>.</param>
public sealed record ApplicationBackgroundPayload(
    Guid AppSessionId,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? WindowTitle,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    double DurationSeconds,
    double InteractionSeconds,
    double IdleSeconds,
    string EndReason,
    int WindowsSessionId);

/// <summary>USER_LOGIN / USER_LOGOUT / SESSION_CONNECT / SESSION_DISCONNECT / SESSION_LOCK / SESSION_UNLOCK.</summary>
public sealed record SessionStatePayload(
    int WindowsSessionId,
    string? UserName,
    string? Domain,
    string? State,
    string? ClientName);

/// <summary>ACTIVE_STARTED / IDLE_STARTED / IDLE_ENDED.</summary>
/// <param name="IdleSinceUtc">For IDLE_STARTED/IDLE_ENDED: the time of the last real input (not the time the threshold elapsed).</param>
/// <param name="IdleSeconds">For IDLE_ENDED: total idle length in seconds.</param>
/// <param name="ThresholdSeconds">The idle threshold in force.</param>
public sealed record IdlePayload(
    int WindowsSessionId,
    string? UserName,
    DateTimeOffset? IdleSinceUtc,
    double? IdleSeconds,
    int ThresholdSeconds);

/// <summary>
/// MOUSE_ACTIVITY / KEYBOARD_ACTIVITY. Counters for one time bucket. Never contains key identities.
/// The server upserts one input_activity row per (device, BucketStartUtc).
/// </summary>
public sealed record InputActivityPayload(
    DateTimeOffset BucketStartUtc,
    int BucketSeconds,
    int? KeyboardEventCount,
    DateTimeOffset? LastKeyboardUtc,
    int? MouseMoveEventCount,
    int? ClickCount,
    int? DoubleClickCount,
    int? WheelCount,
    DateTimeOffset? LastMouseUtc,
    int? CursorX,
    int? CursorY);

/// <summary>USB_CONNECTED / USB_DISCONNECTED.</summary>
public sealed record UsbPayload(
    string DeviceType,
    string? VendorId,
    string? ProductId,
    string? Vendor,
    string? Product,
    string? SerialNumber,
    string? InstanceId,
    string? DriveLetter,
    DateTimeOffset? ArrivedUtc,
    DateTimeOffset? RemovedUtc);

/// <summary>FILE_CREATED / FILE_MODIFIED / FILE_RENAMED / FILE_DELETED. Metadata only - never content.</summary>
public sealed record FilePayload(
    string Path,
    string FileName,
    string? Extension,
    string Action,
    string? OldPath,
    string? UserName);

/// <summary>NETWORK_CONNECTED / NETWORK_DISCONNECTED.</summary>
public sealed record NetworkPayload(
    bool Connected,
    IReadOnlyList<string> IpAddresses,
    string? PrimaryAdapter);

/// <summary>AGENT_STARTED / AGENT_STOPPED / AGENT_ERROR / AGENT_VERSION_CHANGED / AGENT_HEARTBEAT.</summary>
public sealed record AgentPayload(
    string AgentVersion,
    string? PreviousVersion,
    string? Reason,
    string? Message,
    string? Component,
    bool? UnexpectedPreviousShutdown,
    DateTimeOffset? LastKnownAliveUtc,
    // Set only on a deliberate AGENT_STOPPED (service stopped while a user was signed in and the OS was not shutting
    // down). Null for a benign shutdown and absent on older agents, so it can never raise a false tamper alert.
    string? InteractiveUser = null);

/// <summary>Logical disk information inside <see cref="SystemMetricPayload"/>.</summary>
public sealed record DiskInfo(string Name, double TotalGb, double FreeGb, double UsedPercent);

/// <summary>Battery information (laptops).</summary>
public sealed record BatteryInfo(bool Present, int? Percent, bool? Charging, int? MinutesRemaining);

/// <summary>Display information.</summary>
public sealed record DisplayInfo(int Width, int Height);

/// <summary>SYSTEM_METRIC.</summary>
public sealed record SystemMetricPayload(
    double CpuPercent,
    double MemoryUsedPercent,
    double MemoryTotalMb,
    double MemoryAvailableMb,
    IReadOnlyList<DiskInfo> Disks,
    long UptimeSeconds,
    bool NetworkConnected,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> MacAddresses,
    BatteryInfo? Battery,
    IReadOnlyList<DisplayInfo> Displays,
    string? CurrentUser);

/// <summary>One installed program, carried in <see cref="InventoryPayload"/>.</summary>
public sealed record InstalledSoftware(string Name, string? Version, string? Publisher);

/// <summary>
/// DEVICE_INVENTORY: a periodic snapshot (on start + daily) of hardware facts and installed software.
/// Not time-series - the server keeps the latest snapshot per device. All fields optional for forward compatibility.
/// </summary>
public sealed record InventoryPayload(
    string? Manufacturer,
    string? Model,
    string? Serial,
    string? OsName,
    string? OsVersion,
    string? OsBuild,
    string? CpuModel,
    int? CpuCores,
    int? CpuLogical,
    double? RamGb,
    double? DiskTotalGb,
    IReadOnlyList<InstalledSoftware> Software);
