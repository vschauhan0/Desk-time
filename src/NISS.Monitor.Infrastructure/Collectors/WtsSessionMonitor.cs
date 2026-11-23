using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Native;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Collectors;

/// <summary>
/// Login / logout / connect / disconnect detection by polling the WTS session table every few seconds
/// (works in a service and in a console host; no window or message pump needed). Known sessions are persisted so a
/// restart of the agent neither replays logins nor misses a logout that happened while it was down.
/// </summary>
public sealed class WtsSessionMonitor : ICollector
{
    private const string StateKey = "known_sessions";
    private readonly IEventSink _sink;
    private readonly IAgentStateStore _state;
    private readonly ILiveStateStore _live;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<WtsSessionMonitor> _log;
    private readonly SessionDiffEngine _engine = new();
    private readonly Dictionary<int, Guid> _sessionGuids = [];

    public WtsSessionMonitor(IEventSink sink, IAgentStateStore state, ILiveStateStore live, ICollectorRegistry registry, TimeProvider time, ILogger<WtsSessionMonitor> log)
    {
        _sink = sink; _state = state; _live = live; _registry = registry; _time = time; _log = log;
    }

    public string Name => "SessionMonitor";

    /// <summary>Identifier of the current login context for a Windows session (attached to session-scoped events).</summary>
    public Guid GetSessionGuid(int windowsSessionId)
        => _sessionGuids.TryGetValue(windowsSessionId, out var g) ? g : Guid.Empty;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RestoreAsync(cancellationToken).ConfigureAwait(false);
        _registry.Report(Name, "Running");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task RestoreAsync(CancellationToken ct)
    {
        var json = await _state.GetAsync(StateKey, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var known = JsonSerializer.Deserialize<List<SessionDiffEngine.WtsSession>>(json);
            if (known is not null)
            {
                _engine.Seed(known);
                foreach (var k in known) _live.MarkLoggedIn(k.SessionId, k.UserName);
            }
        }
        catch (JsonException) { /* stale format: start fresh */ }
    }

    /// <summary>One poll (public for tests).</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var changes = _engine.Update(Enumerate());
        foreach (var change in changes)
        {
            var s = change.Session;
            var (type, at) = change.Kind switch
            {
                SessionDiffEngine.Login => (EventTypes.UserLogin, s.LogonTimeUtc is { } l && now - l < TimeSpan.FromDays(3) ? l : now),
                SessionDiffEngine.Logout => (EventTypes.UserLogout, now),
                SessionDiffEngine.Connect => (EventTypes.SessionConnect, now),
                _ => (EventTypes.SessionDisconnect, now),
            };
            if (change.Kind == SessionDiffEngine.Login) { _sessionGuids[s.SessionId] = Guid.NewGuid(); _live.MarkLoggedIn(s.SessionId, s.UserName); }
            var sessionGuid = GetSessionGuid(s.SessionId);
            var payload = new SessionStatePayload(s.SessionId, s.UserName, s.Domain, s.State, s.ClientName);
            await _sink.EmitAsync(EventDraft.Create(type, EventSources.Service, at, payload, sessionGuid == Guid.Empty ? null : sessionGuid), ct).ConfigureAwait(false);
            if (change.Kind == SessionDiffEngine.Logout) { _live.Remove(s.SessionId); _sessionGuids.Remove(s.SessionId); }
            _log.LogInformation("Session {SessionId} {Kind} ({User})", s.SessionId, change.Kind, s.UserName);
            _registry.Touch(Name);
        }
        if (changes.Count > 0)
            await _state.SetAsync(StateKey, JsonSerializer.Serialize(_engine.Known.ToList()), ct).ConfigureAwait(false);
    }

    /// <summary>Enumerate sessions that belong to a user.</summary>
    public static List<SessionDiffEngine.WtsSession> Enumerate()
    {
        var result = new List<SessionDiffEngine.WtsSession>();
        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFOW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFOW>(buffer + i * size);
                if (info.SessionId == 0) continue;
                var user = QueryString(info.SessionId, NativeMethods.WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var (logon, client) = QueryInfo(info.SessionId);
                result.Add(new SessionDiffEngine.WtsSession(info.SessionId, StateName(info.State), user, QueryString(info.SessionId, NativeMethods.WTSDomainName), client, logon));
            }
        }
        finally { NativeMethods.WTSFreeMemory(buffer); }
        return result;
    }

    private static string? QueryString(int sessionId, int cls)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, cls, out var buf, out var bytes) || buf == IntPtr.Zero) return null;
        try { return bytes > 2 ? Marshal.PtrToStringUni(buf) : null; }
        finally { NativeMethods.WTSFreeMemory(buf); }
    }

    private static (DateTimeOffset? Logon, string? Client) QueryInfo(int sessionId)
    {
        DateTimeOffset? logon = null;
        if (NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, NativeMethods.WTSSessionInfoClass, out var buf, out var bytes) && buf != IntPtr.Zero)
        {
            try
            {
                if (bytes >= Marshal.SizeOf<NativeMethods.WTSINFOW>())
                {
                    var info = Marshal.PtrToStructure<NativeMethods.WTSINFOW>(buf);
                    if (info.LogonTime > 0) logon = DateTimeOffset.FromFileTime(info.LogonTime).ToUniversalTime();
                }
            }
            finally { NativeMethods.WTSFreeMemory(buf); }
        }
        var client = QueryString(sessionId, NativeMethods.WTSClientName);
        return (logon, string.IsNullOrEmpty(client) ? null : client);
    }

    private static string StateName(int state) => state switch
    {
        0 => "Active", 1 => "Connected", 2 => "ConnectQuery", 3 => "Shadow", 4 => "Disconnected", 5 => "Idle", 6 => "Listen", 7 => "Reset", 8 => "Down", 9 => "Init",
        _ => state.ToString(CultureInfo.InvariantCulture),
    };
}
