using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reporting;

/// <summary>Filter for USB / file event lists.</summary>
public sealed record DeviceEventFilter(Scope Scope, TimeRange Range, string? Action = null, string? Search = null);

/// <summary>USB and file activity lists.</summary>
public interface IEventQueryService
{
    Task<Paged<UsbEventDto>> UsbAsync(DeviceEventFilter filter, PageRequest page, CancellationToken ct);
    Task<Paged<FileEventDto>> FilesAsync(DeviceEventFilter filter, PageRequest page, CancellationToken ct);
}

internal sealed class EventQueryService(IAppDbContext db) : IEventQueryService
{
    public async Task<Paged<UsbEventDto>> UsbAsync(DeviceEventFilter f, PageRequest page, CancellationToken ct)
    {
        var from = f.Range.From; var to = f.Range.To;
        var q = db.UsbEvents.AsNoTracking().InScope(f.Scope).Where(e => e.Timestamp >= from && e.Timestamp < to);
        if (!string.IsNullOrWhiteSpace(f.Action)) { var a = f.Action.Trim().ToUpper(); q = q.Where(e => e.EventType == a || e.EventType == "USB_" + a); }
        return await q.OrderByDescending(e => e.Timestamp).ToPagedAsync(page, async rows =>
        {
            var (devices, employees) = await Lookups(rows.Select(r => r.DeviceId), rows.Select(r => r.EmployeeId), ct);
            return (IReadOnlyList<UsbEventDto>)rows.Select(r => new UsbEventDto(r.Id, devices.GetValueOrDefault(r.DeviceId, ""),
                r.EmployeeId is { } e ? employees.GetValueOrDefault(e) : null, r.EventType, r.DeviceType, r.Vendor, r.Product, r.SerialNumber, r.Timestamp)).ToList();
        }, ct);
    }

    public async Task<Paged<FileEventDto>> FilesAsync(DeviceEventFilter f, PageRequest page, CancellationToken ct)
    {
        var from = f.Range.From; var to = f.Range.To;
        var q = db.FileEvents.AsNoTracking().InScope(f.Scope).Where(e => e.Timestamp >= from && e.Timestamp < to);
        if (!string.IsNullOrWhiteSpace(f.Action)) { var a = f.Action.Trim().ToUpper(); q = q.Where(e => e.Action == a); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var like = QueryExtensions.LikeContains(f.Search.Trim());
            q = q.Where(e => EF.Functions.Like(e.Path.ToLower(), like) || EF.Functions.Like(e.FileName.ToLower(), like));
        }
        return await q.OrderByDescending(e => e.Timestamp).ToPagedAsync(page, async rows =>
        {
            var (devices, employees) = await Lookups(rows.Select(r => r.DeviceId), rows.Select(r => r.EmployeeId), ct);
            return (IReadOnlyList<FileEventDto>)rows.Select(r => new FileEventDto(r.Id, devices.GetValueOrDefault(r.DeviceId, ""),
                r.EmployeeId is { } e ? employees.GetValueOrDefault(e) : null, r.Action, r.Path, r.FileName, r.Extension, r.UserName, r.Timestamp)).ToList();
        }, ct);
    }

    private async Task<(Dictionary<Guid, string>, Dictionary<Guid, string>)> Lookups(IEnumerable<Guid> deviceIds, IEnumerable<Guid?> employeeIds, CancellationToken ct)
    {
        var d = deviceIds.Distinct().ToList();
        var e = employeeIds.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        return (await db.Devices.AsNoTracking().Where(x => d.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DeviceCode, ct),
                await db.Employees.AsNoTracking().Where(x => e.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.EmployeeCode, ct));
    }
}
