using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using ET = NISS.Monitor.Protocol.EventTypes;

namespace NISS.Application.Reporting;

/// <summary>Chronological "what happened" view for a device or an employee.</summary>
public interface ITimelineService
{
    Task<IReadOnlyList<TimelineItem>> GetAsync(Scope scope, TimeRange range, CancellationToken ct);
}

internal sealed class TimelineService(IAppDbContext db, TimeProvider clock) : ITimelineService
{
    private const int MaxEvents = 2000;

    // Types shown from the raw event stream. Foreground/idle come from their session tables (they carry durations);
    // process, input and metric streams are far too chatty for a timeline.
    private static readonly string[] EventKinds =
    [
        ET.UserLogin, ET.UserLogout, ET.SessionLock, ET.SessionUnlock, ET.SessionConnect, ET.SessionDisconnect,
        ET.ApplicationStarted, ET.ApplicationStopped, ET.UsbConnected, ET.UsbDisconnected,
        ET.FileCreated, ET.FileModified, ET.FileRenamed, ET.FileDeleted,
        ET.NetworkConnected, ET.NetworkDisconnected, ET.AgentStarted, ET.AgentStopped, ET.AgentError, ET.AgentVersionChanged,
    ];

    public async Task<IReadOnlyList<TimelineItem>> GetAsync(Scope scope, TimeRange range, CancellationToken ct)
    {
        var from = range.From; var to = range.To;
        var now = clock.GetUtcNow();
        var events = await db.EventIngestions.AsNoTracking().InScope(scope)
            .Where(e => e.Timestamp >= from && e.Timestamp < to && EventKinds.Contains(e.EventType))
            .OrderByDescending(e => e.Timestamp).Take(MaxEvents).ToListAsync(ct);
        var apps = await db.ApplicationSessions.AsNoTracking().InScope(scope)
            .Where(s => s.StartedAt >= from && s.StartedAt < to).OrderByDescending(s => s.StartedAt).Take(MaxEvents).ToListAsync(ct);
        var idles = await db.IdleSessions.AsNoTracking().InScope(scope)
            .Where(s => s.Kind == IdleKinds.Idle && s.StartedAt >= from && s.StartedAt < to).OrderByDescending(s => s.StartedAt).Take(MaxEvents).ToListAsync(ct);

        var deviceIds = events.Select(e => e.DeviceId).Concat(apps.Select(a => a.DeviceId)).Concat(idles.Select(i => i.DeviceId)).Distinct().ToList();
        var devices = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        string Code(Guid id) => devices.TryGetValue(id, out var d) ? d.DeviceCode : "";
        int Offset(Guid id) => devices.TryGetValue(id, out var d) ? d.LocalOffsetMinutes : 0;

        var items = new List<TimelineItem>();
        foreach (var e in events) items.Add(FromEvent(e, Code(e.DeviceId)));
        foreach (var a in apps)
            items.Add(new TimelineItem(a.StartedAt, Offset(a.DeviceId), ET.ApplicationForeground, "APP", $"{Text.NormalizeProcess(a.ProcessName)} in foreground",
                a.WindowTitle, Code(a.DeviceId), a.ProcessName, a.EndedAt is null ? Math.Max(0, (now - a.StartedAt).TotalSeconds) : a.DurationSeconds));
        foreach (var i in idles)
            items.Add(new TimelineItem(i.StartedAt, Offset(i.DeviceId), ET.IdleStarted, "IDLE", "Idle", null, Code(i.DeviceId), null,
                i.EndedAt is null ? Math.Max(0, (now - i.StartedAt).TotalSeconds) : i.DurationSeconds));

        return items.OrderBy(i => i.TimestampUtc).ToList();
    }

    private static TimelineItem FromEvent(EventIngestion e, string deviceCode)
    {
        var (kind, title) = e.EventType switch
        {
            ET.UserLogin => ("LOGIN", "User logged in"),
            ET.UserLogout => ("LOGIN", "User logged out"),
            ET.SessionLock => ("LOCK", "Workstation locked"),
            ET.SessionUnlock => ("LOCK", "Workstation unlocked"),
            ET.SessionConnect => ("SESSION", "Session connected"),
            ET.SessionDisconnect => ("SESSION", "Session disconnected"),
            ET.ApplicationStarted => ("PROCESS", $"{Text.NormalizeProcess(e.Subject ?? "application")} opened"),
            ET.ApplicationStopped => ("PROCESS", $"{Text.NormalizeProcess(e.Subject ?? "application")} closed"),
            ET.UsbConnected => ("USB", "USB device connected"),
            ET.UsbDisconnected => ("USB", "USB device removed"),
            ET.NetworkConnected => ("NETWORK", "Network connected"),
            ET.NetworkDisconnected => ("NETWORK", "Network disconnected"),
            ET.AgentStarted => ("AGENT", "Monitoring agent started"),
            ET.AgentStopped => ("AGENT", "Monitoring agent stopped"),
            ET.AgentError => ("AGENT", "Monitoring agent error"),
            ET.AgentVersionChanged => ("AGENT", "Monitoring agent updated"),
            _ => ("FILE", e.EventType.Replace('_', ' ').ToLowerInvariant()),
        };
        var payload = JsonColumn.ToElement(e.Payload);
        var detail = e.EventType switch
        {
            ET.UserLogin or ET.UserLogout => Prop(payload, "userName"),
            ET.UsbConnected or ET.UsbDisconnected => Prop(payload, "product") ?? Prop(payload, "vendor") ?? Prop(payload, "deviceType"),
            ET.ApplicationStarted or ET.ApplicationStopped => Prop(payload, "windowTitle"),
            ET.AgentError => Prop(payload, "message"),
            ET.FileCreated or ET.FileModified or ET.FileRenamed or ET.FileDeleted => Prop(payload, "fileName") ?? Prop(payload, "path"),
            _ => null,
        };
        return new TimelineItem(e.Timestamp, e.LocalOffsetMinutes, e.EventType, kind, title, detail, deviceCode, e.Subject, null);
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
