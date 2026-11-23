using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using ET = NISS.Monitor.Protocol.EventTypes;

namespace NISS.Application.Ingestion.Projectors;

/// <summary>USER_LOGIN / USER_LOGOUT: <c>user_sessions</c>, <c>device_users</c> and presence.</summary>
internal sealed class UserSessionProjector(IAppDbContext db, IEmployeeResolver employees, ILiveStateWriter live) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.UserLogin, ET.UserLogout];

    public async Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<SessionStatePayload>();
        var user = Text.Trunc(p.UserName, 256);
        var deviceId = ctx.DeviceId;
        var at = ctx.Timestamp;

        await employees.EnsureDeviceUserAsync(ctx.Device, user, ctx.EmployeeId, at, ct);

        if (ctx.EventType == ET.UserLogin) await LoginAsync(ctx, p, user, ct);
        else await LogoutAsync(ctx, p, deviceId, at, ct);
    }

    private async Task LoginAsync(ProjectionContext ctx, SessionStatePayload p, string? user, CancellationToken ct)
    {
        var deviceId = ctx.DeviceId;
        var at = ctx.Timestamp;

        // A LOGOUT that arrived first left a placeholder: complete it instead of creating a second session.
        var placeholder = await db.UserSessions
            .Where(s => s.DeviceId == deviceId && s.WindowsSessionId == p.WindowsSessionId && s.LoginMissing && s.LogoutAt != null && s.LogoutAt >= at)
            .OrderBy(s => s.LogoutAt).FirstOrDefaultAsync(ct);
        if (placeholder is not null)
        {
            placeholder.LoginAt = at; placeholder.LoginMissing = false; placeholder.UserName ??= user; placeholder.EmployeeId ??= ctx.EmployeeId;
            return;
        }

        var alreadyOpen = await db.UserSessions.AnyAsync(s => s.DeviceId == deviceId && s.WindowsSessionId == p.WindowsSessionId && s.LogoutAt == null && s.LoginAt <= at, ct);
        if (!alreadyOpen)
        {
            // A new logon closes idle/lock periods left dangling by a session that ended without a logout event.
            await CloseDanglingAsync(deviceId, at, ct);
            db.UserSessions.Add(new UserSession
            {
                Id = Guid.NewGuid(), DeviceId = deviceId, EmployeeId = ctx.EmployeeId, WindowsSessionId = p.WindowsSessionId, UserName = user, LoginAt = at,
            });
        }
        await live.SetPresenceAsync(deviceId, Presence.Active, at, user, false, ct);
    }

    private async Task LogoutAsync(ProjectionContext ctx, SessionStatePayload p, Guid deviceId, DateTimeOffset at, CancellationToken ct)
    {
        var open = await db.UserSessions
            .Where(s => s.DeviceId == deviceId && s.WindowsSessionId == p.WindowsSessionId && s.LogoutAt == null && s.LoginAt <= at)
            .OrderByDescending(s => s.LoginAt).FirstOrDefaultAsync(ct);
        if (open is not null) open.LogoutAt = at;
        else
        {
            var alreadyClosed = await db.UserSessions.AnyAsync(s => s.DeviceId == deviceId && s.WindowsSessionId == p.WindowsSessionId && s.LogoutAt != null && s.LoginAt <= at && s.LogoutAt >= at, ct);
            if (!alreadyClosed)
                db.UserSessions.Add(new UserSession
                {
                    Id = Guid.NewGuid(), DeviceId = deviceId, EmployeeId = ctx.EmployeeId, WindowsSessionId = p.WindowsSessionId,
                    UserName = Text.Trunc(p.UserName, 256), LoginAt = at, LogoutAt = at, LoginMissing = true,
                });
        }

        await CloseDanglingAsync(deviceId, at, ct);
        await live.SetPresenceAsync(deviceId, Presence.LoggedOut, at, null, true, ct);
    }

    private async Task CloseDanglingAsync(Guid deviceId, DateTimeOffset at, CancellationToken ct)
    {
        var open = await db.IdleSessions.Where(s => s.DeviceId == deviceId && s.EndedAt == null && s.StartedAt <= at).ToListAsync(ct);
        foreach (var s in open) { s.EndedAt = at; s.DurationSeconds = Math.Max(0, (at - s.StartedAt).TotalSeconds); }
    }
}

/// <summary>IDLE_*, ACTIVE_STARTED, SESSION_LOCK/UNLOCK, SESSION_CONNECT/DISCONNECT: <c>idle_sessions</c> and presence.</summary>
internal sealed class IdleSessionProjector(IAppDbContext db, ILiveStateWriter live) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } =
    [
        ET.IdleStarted, ET.IdleEnded, ET.ActiveStarted,
        ET.SessionLock, ET.SessionUnlock,
        ET.SessionConnect, ET.SessionDisconnect,
    ];

    public async Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var at = ctx.Timestamp;
        switch (ctx.EventType)
        {
            case ET.IdleStarted:
            {
                var p = ctx.Payload<IdlePayload>();
                await OpenAsync(ctx, IdleKinds.Idle, p.IdleSinceUtc ?? at, p.WindowsSessionId, ct);
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Idle, at, p.UserName, false, ct);
                break;
            }
            case ET.IdleEnded:
            {
                var p = ctx.Payload<IdlePayload>();
                var hint = p.IdleSinceUtc ?? (p.IdleSeconds is { } s ? at.AddSeconds(-s) : (DateTimeOffset?)null);
                await CloseAsync(ctx, IdleKinds.Idle, at, hint, p.IdleSeconds, p.WindowsSessionId, ct);
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Active, at, p.UserName, false, ct);
                break;
            }
            case ET.ActiveStarted:
            {
                var p = ctx.Payload<IdlePayload>();
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Active, at, p.UserName, false, ct);
                break;
            }
            case ET.SessionLock:
                await OpenAsync(ctx, IdleKinds.Locked, at, ctx.Payload<SessionStatePayload>().WindowsSessionId, ct);
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Locked, at, null, false, ct);
                break;
            case ET.SessionUnlock:
                await CloseAsync(ctx, IdleKinds.Locked, at, null, null, null, ct);
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Active, at, ctx.Payload<SessionStatePayload>().UserName, false, ct);
                break;
            case ET.SessionDisconnect:
                await OpenAsync(ctx, IdleKinds.Disconnected, at, ctx.Payload<SessionStatePayload>().WindowsSessionId, ct);
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Disconnected, at, null, false, ct);
                break;
            case ET.SessionConnect:
                await CloseAsync(ctx, IdleKinds.Disconnected, at, null, null, null, ct);
                await live.SetPresenceAsync(ctx.DeviceId, Presence.Active, at, ctx.Payload<SessionStatePayload>().UserName, false, ct);
                break;
        }
    }

    private async Task OpenAsync(ProjectionContext ctx, string kind, DateTimeOffset start, int? windowsSessionId, CancellationToken ct)
    {
        var deviceId = ctx.DeviceId;
        // Same period already known (open, or closed because its END arrived first): nothing to do.
        var known = await db.IdleSessions.AnyAsync(s => s.DeviceId == deviceId && s.Kind == kind
            && (s.EndedAt == null || (s.StartedAt >= start.AddSeconds(-1) && s.StartedAt <= start.AddSeconds(1))), ct);
        if (known) return;
        db.IdleSessions.Add(new IdleSession { Id = Guid.NewGuid(), DeviceId = deviceId, EmployeeId = ctx.EmployeeId, Kind = kind, StartedAt = start, WindowsSessionId = windowsSessionId });
    }

    private async Task CloseAsync(ProjectionContext ctx, string kind, DateTimeOffset end, DateTimeOffset? startHint, double? reportedSeconds, int? windowsSessionId, CancellationToken ct)
    {
        var deviceId = ctx.DeviceId;
        var open = await db.IdleSessions
            .Where(s => s.DeviceId == deviceId && s.Kind == kind && s.EndedAt == null && s.StartedAt <= end)
            .OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);
        if (open is not null)
        {
            open.EndedAt = end;
            open.DurationSeconds = Math.Max(0, reportedSeconds ?? (end - open.StartedAt).TotalSeconds);
            return;
        }
        if (startHint is not { } start || start > end) return;   // an END without any START information cannot be reconstructed
        var known = await db.IdleSessions.AnyAsync(s => s.DeviceId == deviceId && s.Kind == kind
            && s.StartedAt >= start.AddSeconds(-1) && s.StartedAt <= start.AddSeconds(1), ct);
        if (known) return;
        db.IdleSessions.Add(new IdleSession
        {
            Id = Guid.NewGuid(), DeviceId = deviceId, EmployeeId = ctx.EmployeeId, Kind = kind, StartedAt = start, EndedAt = end,
            DurationSeconds = Math.Max(0, reportedSeconds ?? (end - start).TotalSeconds), WindowsSessionId = windowsSessionId,
        });
    }
}
