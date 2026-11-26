using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core;

/// <summary>Foreground application snapshot taken by the session process.</summary>
public sealed record ForegroundSample(int ProcessId, string ProcessName, string? ExecutablePath, string? WindowTitle);

/// <summary>An application that owns a visible top-level window.</summary>
public sealed record OpenWindowApp(int ProcessId, string ProcessName, string? ExecutablePath, string? WindowTitle);

/// <summary>Minimal process facts obtained cheaply from a toolhelp snapshot.</summary>
public sealed record ProcessSnapshot(int Pid, int ParentPid, string Name, int SessionId);

/// <summary>Identity assigned to this device at enrollment.</summary>
/// <param name="DeviceSecret">Clear-text secret, held in memory only. It is stored DPAPI-protected.</param>
public sealed record DeviceIdentity(
    string DeviceId,
    string DeviceSecret,
    string ServerBaseUrl,
    string? EmployeeId,
    string? CertificateThumbprint,
    DateTimeOffset EnrolledAtUtc);

/// <summary>State reported by a session process for one interactive session.</summary>
public sealed record SessionLiveState(
    int WindowsSessionId,
    string? UserName,
    string Presence,
    ForegroundInfo? Foreground,
    IReadOnlyList<OpenApplication> OpenApplications,
    double IdleSeconds,
    bool Interacting,
    DateTimeOffset? LastInputUtc,
    DateTimeOffset ReportedUtc);

/// <summary>Counters for one input bucket (no key identities, ever).</summary>
public sealed record InputBucket(
    DateTimeOffset StartUtc,
    int Seconds,
    int KeyboardEvents,
    DateTimeOffset? LastKeyboardUtc,
    int MouseMoveEvents,
    int Clicks,
    int DoubleClicks,
    int WheelEvents,
    DateTimeOffset? LastMouseUtc,
    int? CursorX,
    int? CursorY)
{
    /// <summary>True when the bucket contains any keyboard activity.</summary>
    public bool HasKeyboard => KeyboardEvents > 0;

    /// <summary>True when the bucket contains any mouse activity.</summary>
    public bool HasMouse => MouseMoveEvents > 0 || Clicks > 0 || DoubleClicks > 0 || WheelEvents > 0;
}

/// <summary>Result of queue statistics.</summary>
public sealed record QueueStats(int Pending, int DeadLetters, DateTimeOffset? OldestPendingUtc, DateTimeOffset? LastUploadUtc, string? LastError);

/// <summary>An event stored in the local queue.</summary>
public sealed record QueuedEvent(Guid EventId, long Sequence, string EventType, int Attempts, string EnvelopeJson);

/// <summary>Health of one collector as tracked by the supervisor.</summary>
public sealed record CollectorHealth(string Name, string State, string? LastError, DateTimeOffset? LastEventUtc, int Restarts);

/// <summary>Named-pipe message between the session process and the service (JSON lines).</summary>
public sealed class PipeMessage
{
    public const string HelloKind = "hello";
    public const string EventKind = "event";
    public const string StateKind = "state";
    public const string ConfigKind = "config";
    public const string ByeKind = "bye";

    public string Kind { get; set; } = "";
    public int? WindowsSessionId { get; set; }
    public string? UserName { get; set; }
    public int? ProcessId { get; set; }
    public string? Version { get; set; }
    public PipeEvent? Event { get; set; }
    public SessionLiveState? State { get; set; }
    public AgentPolicy? Policy { get; set; }
}

/// <summary>Event draft transported over the pipe.</summary>
public sealed class PipeEvent
{
    public string EventType { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public System.Text.Json.JsonElement Payload { get; set; }
    public Guid? SessionId { get; set; }
}

/// <summary>Well-known names shared by the service, the session process and the CLI.</summary>
public static class AgentConstants
{
    public const string ServiceName = "NISSMonitorService";
    public const string ServiceDisplayName = "NISS Employee Monitoring Service";
    public const string ServiceDescription = "Authorized NISS endpoint telemetry service for managed organizational devices.";
    public const string EventLogSource = "NISS Monitor Service";
    public const string PipeName = "NISSMonitor.Session";
    public const string ScreenPipeName = "NISSMonitor.Screen";
    public const string SessionExeName = "Windows.RunSession.exe";
    public const string ServiceExeName = "Windows.RunService.exe";
    public const string CliExeName = "NISSAgent.exe";
    public const string SessionMutexName = @"Local\NISSMonitor.Session.Singleton";
    public const string RunKeyValueName = "NISSMonitorSession";

    // Mutual watchdog: two independent, ordinary Windows services that watch each other and restart whichever
    // one stops. Both remain fully visible, conventional services (services.msc, sc query, Task Manager's
    // Services tab) - an administrator can always disable both through normal means; this only defeats a casual
    // "End Task" on one of them from Task Manager, not a deliberate, informed admin action against both.
    public const string WatchdogServiceName = "Watchdog";
    public const string WatchdogDisplayName = "Watchdog";
    public const string WatchdogDescription = "Keeps the NISS Employee Monitoring Service running. Authorized NISS endpoint component for managed organizational devices.";
    public const string WatchdogEventLogSource = "NISS Monitor Watchdog";
    public const string WatchdogExeName = "Windows.RunWatchdog.exe";
    public static readonly TimeSpan WatchdogPollInterval = TimeSpan.FromSeconds(5);
}
