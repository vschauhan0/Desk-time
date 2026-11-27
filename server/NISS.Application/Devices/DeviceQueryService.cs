using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Ingestion;
using NISS.Application.Live;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Devices;

/// <summary>Filter for the device list.</summary>
public sealed record DeviceFilter(string? Status, string? Employee, string? Department, string? Search);

/// <summary>Read side for devices.</summary>
public interface IDeviceQueryService
{
    Task<Paged<DeviceSummary>> ListAsync(DeviceFilter filter, PageRequest page, CancellationToken ct);
    Task<DeviceDetail> GetDetailAsync(string deviceCode, CancellationToken ct);
    /// <summary>Resolves a device code such as PC-024 to its id (404 when unknown).</summary>
    Task<Guid> ResolveIdAsync(string deviceCode, CancellationToken ct);
    Task<Paged<EventDto>> ActivityAsync(string deviceCode, TimeRange range, IReadOnlyCollection<string> eventTypes, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<SystemMetricDto>> MetricsAsync(string deviceCode, TimeRange range, int? maxPoints, CancellationToken ct);
    Task<DeviceDiagnostics> DiagnosticsAsync(string deviceCode, CancellationToken ct);
    /// <summary>Latest hardware + installed-software snapshot for a device (404 when none reported yet).</summary>
    Task<DeviceInventoryDto> InventoryAsync(string deviceCode, CancellationToken ct);
}

internal sealed class DeviceQueryService(IAppDbContext db, ISecurityEventService security) : IDeviceQueryService
{
    private sealed class Row { public Device D { get; set; } = null!; public Employee? E { get; set; } public AgentStatus? S { get; set; } }

    private IQueryable<Row> Joined() =>
        from d in db.Devices.AsNoTracking()
        join e in db.Employees.AsNoTracking() on d.EmployeeId equals e.Id into eg
        from e in eg.DefaultIfEmpty()
        join s in db.AgentStatuses.AsNoTracking() on d.Id equals s.DeviceId into sg
        from s in sg.DefaultIfEmpty()
        select new Row { D = d, E = e, S = s };

    public async Task<Guid> ResolveIdAsync(string deviceCode, CancellationToken ct)
    {
        var code = deviceCode.Trim();
        var id = await db.Devices.AsNoTracking().Where(d => d.DeviceCode == code).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        return id ?? throw new NotFoundException($"Device {code} not found.");
    }

    public async Task<Paged<DeviceSummary>> ListAsync(DeviceFilter f, PageRequest page, CancellationToken ct)
    {
        var q = Joined();
        if (!string.IsNullOrWhiteSpace(f.Status)) { var st = f.Status.Trim().ToUpperInvariant(); q = q.Where(x => x.D.Status == st); }
        if (!string.IsNullOrWhiteSpace(f.Employee)) { var code = f.Employee.Trim().ToLower(); q = q.Where(x => x.E != null && x.E.EmployeeCode.ToLower() == code); }
        if (!string.IsNullOrWhiteSpace(f.Department)) { var dept = f.Department.Trim().ToLower(); q = q.Where(x => x.E != null && x.E.Department != null && x.E.Department.ToLower() == dept); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var like = QueryExtensions.LikeContains(f.Search.Trim());
            q = q.Where(x => EF.Functions.Like(x.D.DeviceCode.ToLower(), like) || EF.Functions.Like(x.D.Hostname.ToLower(), like)
                || (x.E != null && (EF.Functions.Like(x.E.FullName.ToLower(), like) || EF.Functions.Like(x.E.EmployeeCode.ToLower(), like))));
        }
        return await q.OrderBy(x => x.D.DeviceCode).ToPagedAsync(page, x => ToSummary(x.D, x.E, x.S), ct);
    }

    public async Task<DeviceDetail> GetDetailAsync(string deviceCode, CancellationToken ct)
    {
        var code = deviceCode.Trim();
        var row = await Joined().FirstOrDefaultAsync(x => x.D.DeviceCode == code, ct) ?? throw new NotFoundException($"Device {code} not found.");
        var users = await db.DeviceUsers.AsNoTracking().Where(u => u.DeviceId == row.D.Id).OrderBy(u => u.WindowsUsername).Select(u => u.WindowsUsername).ToListAsync(ct);
        return new DeviceDetail(
            ToSummary(row.D, row.E, row.S), LiveCardFactory.Build(row.D, row.E, row.S),
            row.D.MachineSerial, row.D.Manufacturer, row.D.Model, row.D.OsVersion, users,
            JsonColumn.DeserializeList<string>(row.D.IpAddressesJson), JsonColumn.DeserializeList<string>(row.D.MacAddressesJson), row.D.LastBootAt);
    }

    public async Task<Paged<EventDto>> ActivityAsync(string deviceCode, TimeRange range, IReadOnlyCollection<string> eventTypes, PageRequest page, CancellationToken ct)
    {
        var id = await ResolveIdAsync(deviceCode, ct);
        var from = range.From; var to = range.To;
        var q = db.EventIngestions.AsNoTracking().Where(e => e.DeviceId == id && e.Timestamp >= from && e.Timestamp < to);
        if (eventTypes.Count > 0)
        {
            var types = eventTypes.Select(t => t.Trim().ToUpperInvariant()).ToList();
            q = q.Where(e => types.Contains(e.EventType));
        }
        var code = deviceCode.Trim();
        return await q.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Sequence).ToPagedAsync(page, async rows =>
        {
            var empIds = rows.Where(r => r.EmployeeId != null).Select(r => r.EmployeeId!.Value).Distinct().ToList();
            var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);
            return (IReadOnlyList<EventDto>)rows.Select(r => EventMapper.ToDto(r, code, r.EmployeeId is { } eid ? emps.GetValueOrDefault(eid) : null)).ToList();
        }, ct);
    }

    public async Task<IReadOnlyList<SystemMetricDto>> MetricsAsync(string deviceCode, TimeRange range, int? maxPoints, CancellationToken ct)
    {
        var id = await ResolveIdAsync(deviceCode, ct);
        var from = range.From; var to = range.To;
        // Read only the columns the chart needs (not the per-disk JSON), then down-sample.
        var rows = await db.SystemMetrics.AsNoTracking().Where(m => m.DeviceId == id && m.Timestamp >= from && m.Timestamp < to)
            .OrderBy(m => m.Timestamp)
            .Select(m => new SystemMetricDto(m.Timestamp, m.CpuPercent, m.MemoryUsedPercent, m.MemoryAvailableMb, m.DiskUsedPercent, m.DiskFreeGb,
                m.NetworkConnected, m.BatteryPercent, m.UptimeSeconds))
            .ToListAsync(ct);
        var limit = Math.Clamp(maxPoints ?? 300, 10, 5000);
        if (rows.Count <= limit) return rows;

        // Down-sample: average inside equally sized buckets.
        var size = (int)Math.Ceiling(rows.Count / (double)limit);
        return rows.Chunk(size).Select(chunk => new SystemMetricDto(
            chunk[chunk.Length / 2].TimestampUtc, Math.Round(chunk.Average(m => m.CpuPercent), 1), Math.Round(chunk.Average(m => m.MemoryUsedPercent), 1),
            Math.Round(chunk.Average(m => m.MemoryAvailableMb), 0), chunk.Where(m => m.DiskUsedPercent != null).Select(m => m.DiskUsedPercent).DefaultIfEmpty().Average(),
            chunk.Where(m => m.DiskFreeGb != null).Select(m => m.DiskFreeGb).DefaultIfEmpty().Average(),
            chunk[^1].NetworkConnected, chunk[^1].BatteryPercent, chunk[^1].UptimeSeconds)).ToList();
    }

    public async Task<DeviceInventoryDto> InventoryAsync(string deviceCode, CancellationToken ct)
    {
        var id = await ResolveIdAsync(deviceCode, ct);
        var code = deviceCode.Trim();
        var inv = await db.DeviceInventories.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == id, ct)
            ?? throw new NotFoundException($"No inventory reported for device {code}.");
        return new DeviceInventoryDto(code, inv.CapturedAtUtc, inv.Manufacturer, inv.Model, inv.Serial,
            inv.OsName, inv.OsVersion, inv.OsBuild, inv.CpuModel, inv.CpuCores, inv.CpuLogical, inv.RamGb, inv.DiskTotalGb,
            inv.Software.Select(s => new InstalledSoftwareDto(s.Name, s.Version, s.Publisher)).ToList());
    }

    public async Task<DeviceDiagnostics> DiagnosticsAsync(string deviceCode, CancellationToken ct)
    {
        var code = deviceCode.Trim();
        var row = await Joined().FirstOrDefaultAsync(x => x.D.DeviceCode == code, ct) ?? throw new NotFoundException($"Device {code} not found.");
        var s = row.S;
        var agents = JsonColumn.DeserializeList<SessionAgentInfo>(s?.SessionAgentsJson);
        var connected = agents.Count(a => a.Connected);
        var sessionState = agents.Count == 0 ? "UNKNOWN" : connected > 0 ? "CONNECTED" : "MISSING";
        var issues = await security.QueryAsync(new SecurityEventFilter(null, null, code, null, null, null), new PageRequest(1, 20), ct);
        var online = row.D.Status is DeviceHealth.Online or DeviceHealth.Warning;
        return new DeviceDiagnostics(
            row.D.DeviceCode, row.D.Hostname, row.D.AgentVersion, s?.ServiceState ?? "", sessionState, agents, s?.LastHeartbeatAt ?? row.D.LastSeen,
            online && (s?.ServerReachable ?? true), s?.QueueDepth ?? 0, s?.DeadLetterCount ?? 0, s?.LastUploadAt, s?.LastError ?? s?.LastUploadError,
            s?.CpuPercent, s?.MemoryUsedPercent, s?.DiskUsedPercent, row.D.OsVersion, JsonColumn.DeserializeList<CollectorStatus>(s?.CollectorsJson),
            s?.ClockOffsetMs ?? 0, issues.Items);
    }

    internal static DeviceSummary ToSummary(Device d, Employee? e, AgentStatus? s)
    {
        var presence = s is not null && d.Status is DeviceHealth.Online or DeviceHealth.Warning ? s.Presence : Presence.Unknown;
        return new DeviceSummary(d.Id, d.DeviceCode, d.Hostname, e?.EmployeeCode, e?.FullName, e?.Department, d.Os, d.AgentVersion, d.Status, presence,
            d.LastSeen, d.CreatedAt, s?.CurrentApplication, s?.CurrentWindow, s?.CpuPercent, s?.MemoryUsedPercent);
    }
}
