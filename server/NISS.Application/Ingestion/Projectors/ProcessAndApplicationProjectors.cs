using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using ET = NISS.Monitor.Protocol.EventTypes;

namespace NISS.Application.Ingestion.Projectors;

/// <summary>PROCESS_STARTED / PROCESS_STOPPED: raw event row plus the process lifetime (<c>process_sessions</c>).</summary>
internal sealed class ProcessProjector(IAppDbContext db) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.ProcessStarted, ET.ProcessStopped];

    public async Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<ProcessPayload>();
        var started = ctx.EventType == ET.ProcessStarted;
        var name = Text.Trunc(p.ProcessName, 260)!;

        db.ProcessEvents.Add(new ProcessEvent
        {
            Id = ctx.Envelope.EventId, DeviceId = ctx.DeviceId, ProcessSessionId = p.ProcessSessionId, ProcessId = p.ProcessId,
            ParentProcessId = p.ParentProcessId, ProcessName = name, ExecutablePath = Text.Trunc(p.ExecutablePath, 1024),
            EventType = ctx.EventType, Timestamp = ctx.Timestamp,
        });

        var row = await db.ProcessSessions.FindAsync([p.ProcessSessionId], ct);
        if (row is null)
        {
            row = new ProcessSession { Id = p.ProcessSessionId, DeviceId = ctx.DeviceId, StartedAt = p.StartedUtc == default ? ctx.Timestamp : p.StartedUtc };
            db.ProcessSessions.Add(row);
        }

        row.EmployeeId ??= ctx.EmployeeId;
        row.ProcessId = p.ProcessId;
        row.ParentProcessId ??= p.ParentProcessId;
        row.ProcessName = name;
        row.ExecutablePath ??= Text.Trunc(p.ExecutablePath, 1024);
        row.UserName ??= Text.Trunc(p.UserName, 256);
        row.WindowsSessionId = p.WindowsSessionId;
        row.CommandLine ??= Text.Trunc(p.CommandLine, 2000);
        row.Sha256 ??= Text.Trunc(p.Sha256, 64);
        row.Baseline |= p.Baseline;
        if (p.StartedUtc != default) row.StartedAt = p.StartedUtc;

        if (!started) row.EndedAt = p.EndedUtc ?? ctx.Timestamp;          // STARTED arriving after STOPPED never re-opens the row
        else if (p.EndedUtc is { } ended) row.EndedAt ??= ended;
    }
}

/// <summary>APPLICATION_FOREGROUND / APPLICATION_BACKGROUND: foreground sessions (<c>application_sessions</c>) plus live state.</summary>
internal sealed class ApplicationSessionProjector(IAppDbContext db, ILiveStateWriter live) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } =
        [ET.ApplicationForeground, ET.ApplicationBackground];

    public Task ProjectAsync(ProjectionContext ctx, CancellationToken ct) =>
        ctx.EventType == ET.ApplicationForeground ? ForegroundAsync(ctx, ct) : BackgroundAsync(ctx, ct);

    private async Task ForegroundAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<ApplicationForegroundPayload>();
        var name = Text.Trunc(p.ProcessName, 260)!;

        // Close sessions left open by a lost BACKGROUND event (an authoritative BACKGROUND may still overwrite the numbers later).
        var deviceId = ctx.DeviceId;
        var at = ctx.Timestamp;
        var dangling = await db.ApplicationSessions
            .Where(s => s.DeviceId == deviceId && s.EndedAt == null && s.Id != p.AppSessionId && s.StartedAt < at)
            .ToListAsync(ct);
        foreach (var s in dangling)
        {
            s.EndedAt = at;
            s.DurationSeconds = Math.Max(0, (at - s.StartedAt).TotalSeconds);
            s.EndReason = AppSessionEndReasons.Switched;
        }

        var row = await db.ApplicationSessions.FindAsync([p.AppSessionId], ct);
        if (row is null)
        {
            db.ApplicationSessions.Add(new ApplicationSession
            {
                Id = p.AppSessionId, DeviceId = ctx.DeviceId, EmployeeId = ctx.EmployeeId, ProcessId = p.ProcessId, ProcessName = name,
                ExecutablePath = Text.Trunc(p.ExecutablePath, 1024), WindowTitle = Text.Trunc(p.WindowTitle, 512), StartedAt = ctx.Timestamp,
            });
        }
        else
        {
            // BACKGROUND already arrived (out of order): keep the closed session, only fill gaps.
            row.WindowTitle ??= Text.Trunc(p.WindowTitle, 512);
            row.ExecutablePath ??= Text.Trunc(p.ExecutablePath, 1024);
            row.EmployeeId ??= ctx.EmployeeId;
        }

        await live.SetForegroundAsync(ctx.DeviceId, p.ProcessId, name, p.ExecutablePath, p.WindowTitle, ctx.Timestamp, ct);
    }

    private async Task BackgroundAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<ApplicationBackgroundPayload>();
        var name = Text.Trunc(p.ProcessName, 260)!;
        var row = await db.ApplicationSessions.FindAsync([p.AppSessionId], ct);
        if (row is null)
        {
            row = new ApplicationSession { Id = p.AppSessionId, DeviceId = ctx.DeviceId };
            db.ApplicationSessions.Add(row);
        }

        var duration = Math.Max(0, p.DurationSeconds);
        row.EmployeeId ??= ctx.EmployeeId;
        row.ProcessId = p.ProcessId;
        row.ProcessName = name;
        row.ExecutablePath = Text.Trunc(p.ExecutablePath, 1024) ?? row.ExecutablePath;
        row.WindowTitle = Text.Trunc(p.WindowTitle, 512) ?? row.WindowTitle;
        row.StartedAt = p.StartedUtc;
        row.EndedAt = p.EndedUtc;
        row.DurationSeconds = duration;
        row.InteractionSeconds = Math.Clamp(p.InteractionSeconds, 0, duration);
        row.IdleSeconds = Math.Clamp(p.IdleSeconds, 0, duration);
        row.EndReason = Text.Trunc(p.EndReason, 32);

        await live.ClearForegroundAsync(ctx.DeviceId, p.ProcessId, ctx.Timestamp, ct);
    }
}
