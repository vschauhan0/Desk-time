using System.Collections.Frozen;

namespace NISS.Monitor.Protocol;

/// <summary>Strongly typed set of event type names carried in <see cref="EventEnvelope.EventType"/>.</summary>
public static class EventTypes
{
    // Processes (service)
    public const string ProcessStarted = "PROCESS_STARTED";
    public const string ProcessStopped = "PROCESS_STOPPED";

    // Applications (session agent). An "application" is a process that owns a visible top-level window.
    public const string ApplicationStarted = "APPLICATION_STARTED";
    public const string ApplicationStopped = "APPLICATION_STOPPED";
    public const string ApplicationForeground = "APPLICATION_FOREGROUND";
    public const string ApplicationBackground = "APPLICATION_BACKGROUND";

    // Windows session / presence
    public const string UserLogin = "USER_LOGIN";
    public const string UserLogout = "USER_LOGOUT";
    public const string SessionLock = "SESSION_LOCK";
    public const string SessionUnlock = "SESSION_UNLOCK";
    public const string SessionConnect = "SESSION_CONNECT";
    public const string SessionDisconnect = "SESSION_DISCONNECT";
    public const string ActiveStarted = "ACTIVE_STARTED";
    public const string IdleStarted = "IDLE_STARTED";
    public const string IdleEnded = "IDLE_ENDED";

    // Input activity metrics (counts only - never key identities)
    public const string MouseActivity = "MOUSE_ACTIVITY";
    public const string KeyboardActivity = "KEYBOARD_ACTIVITY";

    // Devices
    public const string UsbConnected = "USB_CONNECTED";
    public const string UsbDisconnected = "USB_DISCONNECTED";

    // Files (optional module, default off)
    public const string FileCreated = "FILE_CREATED";
    public const string FileModified = "FILE_MODIFIED";
    public const string FileRenamed = "FILE_RENAMED";
    public const string FileDeleted = "FILE_DELETED";

    // Machine
    public const string SystemMetric = "SYSTEM_METRIC";
    public const string NetworkConnected = "NETWORK_CONNECTED";
    public const string NetworkDisconnected = "NETWORK_DISCONNECTED";

    // Inventory (periodic snapshot: hardware facts + installed software; not time-series)
    public const string DeviceInventory = "DEVICE_INVENTORY";

    // Agent lifecycle
    public const string AgentStarted = "AGENT_STARTED";
    public const string AgentStopped = "AGENT_STOPPED";
    public const string AgentHeartbeat = "AGENT_HEARTBEAT";
    public const string AgentError = "AGENT_ERROR";
    public const string AgentVersionChanged = "AGENT_VERSION_CHANGED";

    /// <summary>Every event type the server accepts.</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        ProcessStarted, ProcessStopped,
        ApplicationStarted, ApplicationStopped, ApplicationForeground, ApplicationBackground,
        UserLogin, UserLogout, SessionLock, SessionUnlock, SessionConnect, SessionDisconnect,
        ActiveStarted, IdleStarted, IdleEnded,
        MouseActivity, KeyboardActivity,
        UsbConnected, UsbDisconnected,
        FileCreated, FileModified, FileRenamed, FileDeleted,
        SystemMetric, NetworkConnected, NetworkDisconnected,
        DeviceInventory,
        AgentStarted, AgentStopped, AgentHeartbeat, AgentError, AgentVersionChanged,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when <paramref name="eventType"/> is a known event type.</summary>
    public static bool IsKnown(string? eventType) => eventType is not null && All.Contains(eventType);
}

/// <summary>Source component names used in <see cref="EventEnvelope.Source"/>.</summary>
public static class EventSources
{
    public const string Service = "NISSMonitor.Service";
    public const string Session = "NISSMonitor.Session";
}

/// <summary>Health states the server computes for a device (see docs/architecture.md).</summary>
public static class DeviceHealth
{
    public const string Pending = "PENDING";              // enrolled, never heard from
    public const string Online = "ONLINE";
    public const string Warning = "WARNING";              // heartbeat delayed (> 60s by default)
    public const string Offline = "OFFLINE";              // no heartbeat (> 5 min by default)
    public const string ServiceStopped = "SERVICE_STOPPED"; // agent reported a graceful stop
    public const string Disabled = "DISABLED";
    public const string Revoked = "REVOKED";
}

/// <summary>Presence of the human at the device. Distinct from "process running" and "app in foreground".</summary>
public static class Presence
{
    public const string Active = "ACTIVE";
    public const string Idle = "IDLE";
    public const string Locked = "LOCKED";
    public const string Disconnected = "DISCONNECTED";
    public const string LoggedOut = "LOGGED_OUT";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Kinds of non-active periods stored in idle_sessions.</summary>
public static class IdleKinds
{
    public const string Idle = "IDLE";
    public const string Locked = "LOCKED";
    public const string Disconnected = "DISCONNECTED";
}

/// <summary>Reasons an application (foreground) session ended.</summary>
public static class AppSessionEndReasons
{
    public const string Switched = "SWITCHED";
    public const string Locked = "LOCKED";
    public const string Logout = "LOGOUT";
    public const string Shutdown = "SHUTDOWN";
    public const string ProcessExited = "PROCESS_EXITED";
    public const string Suspended = "SUSPENDED";
}

/// <summary>Roles for dashboard users.</summary>
public static class Roles
{
    public const string SuperAdmin = "SUPERADMIN";
    public const string Admin = "ADMIN";
    public const string Viewer = "VIEWER";
}

/// <summary>Security / agent-health event kinds raised by the server.</summary>
public static class SecurityEventKinds
{
    public const string AuthFailure = "AUTH_FAILURE";
    public const string AgentBacklogWarning = "AGENT_BACKLOG_WARNING";
    public const string HeartbeatDelayed = "HEARTBEAT_DELAYED";
    public const string DeviceOffline = "DEVICE_OFFLINE";
    public const string DeviceRecovered = "DEVICE_RECOVERED";
    public const string ServiceStopped = "SERVICE_STOPPED";
    /// <summary>A deliberate stop/removal of the agent while a user was signed in (layer-3 anti-tamper); Critical, and
    /// never auto-resolved - it stays until a human acknowledges it, so a watchdog restart cannot silently clear it.</summary>
    public const string TamperSuspected = "TAMPER_SUSPECTED";
    public const string SessionAgentMissing = "SESSION_AGENT_MISSING";
    public const string AgentOutdated = "AGENT_OUTDATED";
    public const string UploadFailure = "UPLOAD_FAILURE";
    public const string UnexpectedShutdown = "UNEXPECTED_SHUTDOWN";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string ClockSkew = "CLOCK_SKEW";
    public const string CollectorFailure = "COLLECTOR_FAILURE";
}
