using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Health;

/// <summary>Minimal identity of a device for messages and notifications.</summary>
public readonly record struct DeviceRef(Guid Id, string Code);

/// <summary>Collects notifications while a unit of work runs and publishes them only after the database commit succeeded.</summary>
public interface INotificationBuffer
{
    void Add(SecurityEventDto dto);
    void Add(DeviceStatusChange change);
    void Add(EventDto dto);
    /// <summary>Publishes and clears everything collected so far. Never throws (failures are logged).</summary>
    Task FlushAsync(CancellationToken ct = default);
    void Clear();
}

internal sealed class NotificationBuffer(IRealtimeNotifier notifier, ILogger<NotificationBuffer> log) : INotificationBuffer
{
    private readonly List<SecurityEventDto> _security = [];
    private readonly List<DeviceStatusChange> _status = [];
    private readonly List<EventDto> _events = [];

    public void Add(SecurityEventDto dto) => _security.Add(dto);
    public void Add(DeviceStatusChange change) => _status.Add(change);
    public void Add(EventDto dto) => _events.Add(dto);

    public void Clear() { _security.Clear(); _status.Clear(); _events.Clear(); }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var security = _security.ToArray(); var status = _status.ToArray(); var events = _events.ToArray();
        Clear();
        try
        {
            foreach (var s in status) await notifier.DeviceStatusChangedAsync(s, ct);
            foreach (var s in security) await notifier.SecurityEventRaisedAsync(s, ct);
            foreach (var e in events) await notifier.EventIngestedAsync(e, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Publishing real-time notifications failed");
        }
    }
}

/// <summary>Raises and resolves security / agent-health events.</summary>
public interface ISecurityEventService
{
    /// <summary>
    /// Stages a new event. When <paramref name="onlyIfNoneOpen"/> is true nothing is raised while an unresolved event of the same
    /// kind exists for the device; <paramref name="throttle"/> suppresses repeats within the window. Returns null when suppressed.
    /// The caller persists with SaveChanges and flushes <see cref="INotificationBuffer"/>.
    /// </summary>
    Task<SecurityEvent?> RaiseAsync(DeviceRef? device, string kind, SecuritySeverity severity, string message,
        bool onlyIfNoneOpen = true, TimeSpan? throttle = null, bool alreadyResolved = false, CancellationToken ct = default);

    /// <summary>Stages resolution of open events of the given kinds for the device.</summary>
    Task ResolveAsync(Guid deviceId, CancellationToken ct, params string[] kinds);

    Task<Paged<SecurityEventDto>> QueryAsync(SecurityEventFilter filter, PageRequest page, CancellationToken ct);
    Task<SecurityEventDto> ResolveByIdAsync(Guid id, CancellationToken ct);
}

/// <summary>Filter for <see cref="ISecurityEventService.QueryAsync"/>.</summary>
public sealed record SecurityEventFilter(DateTimeOffset? From, DateTimeOffset? To, string? Device, string? Kind, string? Severity, bool? Resolved);

internal sealed class SecurityEventService(IAppDbContext db, TimeProvider clock, INotificationBuffer buffer, IAuditService audit)
    : ISecurityEventService
{
    public async Task<SecurityEvent?> RaiseAsync(DeviceRef? device, string kind, SecuritySeverity severity, string message,
        bool onlyIfNoneOpen = true, TimeSpan? throttle = null, bool alreadyResolved = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var deviceId = device?.Id;
        if (onlyIfNoneOpen && await db.SecurityEvents.AnyAsync(e => e.DeviceId == deviceId && e.Kind == kind && e.ResolvedAt == null, ct))
            return null;
        if (throttle is { } window)
        {
            var since = now - window;
            if (await db.SecurityEvents.AnyAsync(e => e.DeviceId == deviceId && e.Kind == kind && e.RaisedAt > since, ct)) return null;
        }

        var row = new SecurityEvent
        {
            Id = Guid.NewGuid(), DeviceId = deviceId, Kind = kind, Severity = severity,
            Message = Text.Trunc(message, 1000) ?? "", RaisedAt = now, ResolvedAt = alreadyResolved ? now : null,
        };
        db.SecurityEvents.Add(row);
        buffer.Add(ToDto(row, device?.Code));
        return row;
    }

    public async Task ResolveAsync(Guid deviceId, CancellationToken ct, params string[] kinds)
    {
        var now = clock.GetUtcNow();
        var open = await db.SecurityEvents.Where(e => e.DeviceId == deviceId && e.ResolvedAt == null && kinds.Contains(e.Kind)).ToListAsync(ct);
        foreach (var e in open) e.ResolvedAt = now;
    }

    public async Task<Paged<SecurityEventDto>> QueryAsync(SecurityEventFilter f, PageRequest page, CancellationToken ct)
    {
        var q = db.SecurityEvents.AsNoTracking().AsQueryable();
        if (f.From is { } from) q = q.Where(e => e.RaisedAt >= from.ToUniversalTime());
        if (f.To is { } to) q = q.Where(e => e.RaisedAt < to.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(f.Device))
        {
            var code = f.Device.Trim();
            q = q.Where(e => db.Devices.Any(d => d.Id == e.DeviceId && d.DeviceCode == code));
        }
        if (!string.IsNullOrWhiteSpace(f.Kind)) q = q.Where(e => e.Kind == f.Kind.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(f.Severity) && Enum.TryParse<SecuritySeverity>(f.Severity, true, out var sev)) q = q.Where(e => e.Severity == sev);
        if (f.Resolved is { } resolved) q = resolved ? q.Where(e => e.ResolvedAt != null) : q.Where(e => e.ResolvedAt == null);

        var ordered = q.OrderByDescending(e => e.RaisedAt);
        return await ordered.ToPagedAsync(page, async rows =>
        {
            var ids = rows.Where(r => r.DeviceId != null).Select(r => r.DeviceId!.Value).Distinct().ToList();
            var codes = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DeviceCode, ct);
            return (IReadOnlyList<SecurityEventDto>)rows.Select(r => ToDto(r, r.DeviceId is { } id && codes.TryGetValue(id, out var c) ? c : null)).ToList();
        }, ct);
    }

    public async Task<SecurityEventDto> ResolveByIdAsync(Guid id, CancellationToken ct)
    {
        var row = await db.SecurityEvents.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Security event not found.");
        row.ResolvedAt ??= clock.GetUtcNow();
        audit.Record(AuditActions.SecurityEventResolved, id.ToString(), row.Kind);
        await db.SaveChangesAsync(ct);
        var code = row.DeviceId is null ? null : await db.Devices.Where(d => d.Id == row.DeviceId).Select(d => d.DeviceCode).FirstOrDefaultAsync(ct);
        return ToDto(row, code);
    }

    internal static SecurityEventDto ToDto(SecurityEvent e, string? deviceCode) =>
        new(e.Id, deviceCode, e.Kind, e.Severity.ToString().ToUpperInvariant(), e.Message, e.RaisedAt, e.ResolvedAt);
}
