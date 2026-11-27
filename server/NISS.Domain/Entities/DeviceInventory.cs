namespace NISS.Domain.Entities;

/// <summary>
/// Latest hardware + installed-software snapshot for one device. One row per device, upserted each time the agent
/// reports a DEVICE_INVENTORY event (on start and daily). This is asset state, not time-series telemetry.
/// </summary>
public sealed class DeviceInventory
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsBuild { get; set; }
    public string? CpuModel { get; set; }
    public int? CpuCores { get; set; }
    public int? CpuLogical { get; set; }
    public double? RamGb { get; set; }
    public double? DiskTotalGb { get; set; }

    /// <summary>Installed programs, stored as a JSON column.</summary>
    public List<InstalledSoftwareRow> Software { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One installed program inside a <see cref="DeviceInventory"/> (persisted as JSON, no own table).</summary>
public sealed class InstalledSoftwareRow
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
}
