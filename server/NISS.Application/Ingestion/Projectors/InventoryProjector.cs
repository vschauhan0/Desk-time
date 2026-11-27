using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using ET = NISS.Monitor.Protocol.EventTypes;

namespace NISS.Application.Ingestion.Projectors;

/// <summary>
/// DEVICE_INVENTORY: upsert the latest hardware + installed-software snapshot for the device. One row per device
/// (the unique device index), overwritten on each snapshot - this is asset state, not time-series telemetry.
/// </summary>
internal sealed class DeviceInventoryProjector(IAppDbContext db) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.DeviceInventory];

    public async Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<InventoryPayload>();
        var row = await db.DeviceInventories.FirstOrDefaultAsync(x => x.DeviceId == ctx.DeviceId, ct);
        if (row is null)
        {
            row = new DeviceInventory { Id = Guid.NewGuid(), DeviceId = ctx.DeviceId, CreatedAt = ctx.Timestamp };
            db.DeviceInventories.Add(row);
        }
        row.CapturedAtUtc = ctx.Timestamp;
        row.Manufacturer = Text.Trunc(p.Manufacturer, 256);
        row.Model = Text.Trunc(p.Model, 256);
        row.Serial = Text.Trunc(p.Serial, 256);
        row.OsName = Text.Trunc(p.OsName, 128);
        row.OsVersion = Text.Trunc(p.OsVersion, 64);
        row.OsBuild = Text.Trunc(p.OsBuild, 64);
        row.CpuModel = Text.Trunc(p.CpuModel, 256);
        row.CpuCores = p.CpuCores;
        row.CpuLogical = p.CpuLogical;
        row.RamGb = p.RamGb;
        row.DiskTotalGb = p.DiskTotalGb;
        row.Software = (p.Software ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Take(2000)
            .Select(s => new InstalledSoftwareRow
            {
                Name = Text.Trunc(s.Name, 256)!,
                Version = Text.Trunc(s.Version, 64),
                Publisher = Text.Trunc(s.Publisher, 256),
            })
            .ToList();
    }
}
