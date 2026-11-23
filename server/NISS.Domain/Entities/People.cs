namespace NISS.Domain.Entities;

/// <summary>A person whose managed PC is monitored.</summary>
public sealed class Employee
{
    public Guid Id { get; set; }
    /// <summary>Business identifier such as EMP1024 (unique, case-insensitive).</summary>
    public string EmployeeCode { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Department { get; set; }
    public string? Email { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A managed PC running the NISS endpoint agent.</summary>
public sealed class Device
{
    public Guid Id { get; set; }
    /// <summary>Human-friendly server-assigned id such as PC-024 (column <c>device_id</c>).</summary>
    public string DeviceCode { get; set; } = "";
    public string Hostname { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string AgentVersion { get; set; } = "";
    /// <summary>PENDING/ONLINE/WARNING/OFFLINE/SERVICE_STOPPED/DISABLED/REVOKED (see <c>DeviceHealth</c>).</summary>
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? MachineGuid { get; set; }
    public string? MachineSerial { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>SHA-256 of the 32-byte device secret. The secret itself is never stored.</summary>
    public byte[]? SecretHash { get; set; }
    public DateTimeOffset? SecretRotatedAt { get; set; }
    /// <summary>Time of the last graceful AGENT_STOPPED event that no later heartbeat has superseded.</summary>
    public DateTimeOffset? ServiceStoppedAt { get; set; }
    public DateTimeOffset? LastBootAt { get; set; }
    /// <summary>Last known local UTC offset of the device in minutes (for presentation).</summary>
    public int LocalOffsetMinutes { get; set; }
    public string? IpAddressesJson { get; set; }
    public string? MacAddressesJson { get; set; }
}

/// <summary>Windows account seen on a device, optionally mapped to an employee.</summary>
public sealed class DeviceUser
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string WindowsUsername { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
