namespace NISS.Domain.Entities;

/// <summary>An interactive Windows logon (USER_LOGIN .. USER_LOGOUT).</summary>
public sealed class UserSession : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public int WindowsSessionId { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset LoginAt { get; set; }
    public DateTimeOffset? LogoutAt { get; set; }
    /// <summary>True when a LOGOUT was seen before its LOGIN (LoginAt is a placeholder until the LOGIN arrives).</summary>
    public bool LoginMissing { get; set; }
}

/// <summary>Raw process lifecycle event.</summary>
public sealed class ProcessEvent
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? ProcessSessionId { get; set; }
    public int ProcessId { get; set; }
    public int? ParentProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string EventType { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>One process lifetime, keyed by the agent's ProcessSessionId.</summary>
public sealed class ProcessSession : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public int ProcessId { get; set; }
    public int? ParentProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? UserName { get; set; }
    public int WindowsSessionId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? CommandLine { get; set; }
    public string? Sha256 { get; set; }
    public bool Baseline { get; set; }
}

/// <summary>A foreground application session (time in front), keyed by the agent's AppSessionId.</summary>
public sealed class ApplicationSession : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? WindowTitle { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public double InteractionSeconds { get; set; }
    public double IdleSeconds { get; set; }
    public string? EndReason { get; set; }
    public string SessionType { get; set; } = "FOREGROUND";
}

/// <summary>Input activity counters for one time bucket. Counts only - never key identities.</summary>
public sealed class InputActivity : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    /// <summary>Bucket start (unique per device).</summary>
    public DateTimeOffset Timestamp { get; set; }
    public int BucketSeconds { get; set; }
    public int KeyboardActivity { get; set; }
    public int MouseActivity { get; set; }
    public int ClickCount { get; set; }
    public int DoubleClickCount { get; set; }
    public int WheelCount { get; set; }
    public DateTimeOffset? LastKeyboardAt { get; set; }
    public DateTimeOffset? LastMouseAt { get; set; }
    public int? OptionalCursorX { get; set; }
    public int? OptionalCursorY { get; set; }
}

/// <summary>A non-active period: IDLE, LOCKED or DISCONNECTED.</summary>
public sealed class IdleSession : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public string Kind { get; set; } = "IDLE";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public int? WindowsSessionId { get; set; }
}

/// <summary>Periodic machine metric sample.</summary>
public sealed class SystemMetric
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryUsedPercent { get; set; }
    public double MemoryTotalMb { get; set; }
    public double MemoryAvailableMb { get; set; }
    public double? DiskUsedPercent { get; set; }
    public double? DiskFreeGb { get; set; }
    public bool? NetworkConnected { get; set; }
    public int? BatteryPercent { get; set; }
    public long UptimeSeconds { get; set; }
    public string? DisksJson { get; set; }
}

/// <summary>USB connect/disconnect record.</summary>
public sealed class UsbEvent : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public string EventType { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? Vendor { get; set; }
    public string? Product { get; set; }
    public string? SerialNumber { get; set; }
    public string? InstanceId { get; set; }
    public string? DriveLetter { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>File activity metadata (never contents).</summary>
public sealed class FileEvent : IDeviceOwned
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    public string Action { get; set; } = "";
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public string FileName { get; set; } = "";
    public string? Extension { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>Rows that belong to a device and (when resolvable) an employee. Lets queries be scoped generically.</summary>
public interface IDeviceOwned
{
    Guid DeviceId { get; }
    Guid? EmployeeId { get; }
}
