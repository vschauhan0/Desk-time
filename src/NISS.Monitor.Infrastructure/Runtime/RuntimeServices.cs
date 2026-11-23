using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Runtime;

/// <summary>Hands out monotonically increasing per-device sequence numbers (seeded from the local queue).</summary>
public sealed class SequenceGenerator
{
    private long _value;
    private int _seeded;

    /// <summary>Seed from the highest persisted sequence (idempotent).</summary>
    public void Seed(long max)
    {
        if (Interlocked.CompareExchange(ref _seeded, 1, 0) == 0) Interlocked.Exchange(ref _value, max);
    }

    /// <summary>Next sequence number.</summary>
    public long Next() => Interlocked.Increment(ref _value);
}

/// <summary>Builds complete envelopes: identity, sequence, clock correction, local timestamp, employee.</summary>
public sealed class EventFactory : IEventFactory
{
    private readonly DeviceIdentityStore _identity;
    private readonly SequenceGenerator _sequence;
    private readonly ClockOffsetTracker _clock;
    private readonly IEventQueue _queue;
    private readonly SemaphoreSlim _seedGate = new(1, 1);

    public EventFactory(DeviceIdentityStore identity, SequenceGenerator sequence, ClockOffsetTracker clock, IEventQueue queue)
    {
        _identity = identity;
        _sequence = sequence;
        _clock = clock;
        _queue = queue;
    }

    /// <inheritdoc />
    public async Task<EventEnvelope?> CreateAsync(EventDraft draft, CancellationToken cancellationToken)
    {
        var id = _identity.Current;
        if (id is null) return null;

        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _sequence.Seed(await _queue.GetMaxSequenceAsync(cancellationToken).ConfigureAwait(false)); }
        finally { _seedGate.Release(); }

        var corrected = _clock.Correct(draft.TimestampUtc.ToUniversalTime());
        var local = corrected.ToLocalTime();
        return new EventEnvelope(
            draft.EventId ?? Guid.NewGuid(), SchemaVersions.Current, id.DeviceId, id.EmployeeId, draft.SessionId,
            draft.EventType, corrected, local, draft.Source, _sequence.Next(), _clock.OffsetMs, draft.Payload);
    }
}

/// <summary>Bounded channel between collectors and the persisting worker. Collectors never wait on SQLite.</summary>
public sealed class ChannelEventSink : IEventSink
{
    private readonly Channel<EventDraft> _channel = Channel.CreateBounded<EventDraft>(new BoundedChannelOptions(20_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    /// <summary>Reader side, consumed by the event pipeline worker.</summary>
    public ChannelReader<EventDraft> Reader => _channel.Reader;

    /// <inheritdoc />
    public ValueTask EmitAsync(EventDraft draft, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(draft, cancellationToken);
}

/// <summary>Effective policy: local defaults overlaid by the last policy received from the server.</summary>
public sealed class PolicyProvider : IPolicyProvider
{
    private const string StateKey = "policy_json";
    private readonly IAgentStateStore _state;
    private readonly ILogger<PolicyProvider> _log;
    private volatile AgentPolicy _current;

    public PolicyProvider(IAgentStateStore state, IOptions<AgentSettings> settings, ILogger<PolicyProvider> log)
    {
        _state = state;
        _log = log;
        var m = settings.Value.Monitoring;
        _current = new AgentPolicy
        {
            HeartbeatSeconds = m.HeartbeatSeconds,
            MetricsSeconds = m.SystemMetricsSeconds,
            ForegroundPollMilliseconds = m.ForegroundPollMilliseconds,
            IdleThresholdMinutes = m.IdleThresholdMinutes,
        };
        try
        {
            var json = _state.GetAsync(StateKey, CancellationToken.None).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<AgentPolicy>(json, ProtocolJson.Options) is { } stored) _current = stored;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Stored policy could not be read; using local defaults");
        }
    }

    /// <inheritdoc />
    public AgentPolicy Current => _current;

    /// <inheritdoc />
    public event Action<AgentPolicy>? Changed;

    /// <inheritdoc />
    public async Task UpdateAsync(AgentPolicy policy, CancellationToken cancellationToken)
    {
        var changed = !JsonSerializer.Serialize(policy, ProtocolJson.Options).Equals(JsonSerializer.Serialize(_current, ProtocolJson.Options), StringComparison.Ordinal);
        _current = policy;
        await _state.SetAsync(StateKey, JsonSerializer.Serialize(policy, ProtocolJson.Options), cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            _log.LogInformation("Monitoring policy updated to version {Version}", policy.Version);
            Changed?.Invoke(policy);
        }
    }
}

/// <summary>Merges the state reported by session processes into the heartbeat's live state.</summary>
public sealed class LiveStateStore : ILiveStateStore
{
    private readonly ConcurrentDictionary<int, SessionLiveState> _sessions = new();
    private readonly ConcurrentDictionary<int, string?> _loggedIn = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _stale;

    public LiveStateStore(TimeProvider time, IOptions<AgentSettings> settings)
    {
        _time = time;
        _stale = TimeSpan.FromSeconds(Math.Max(5, settings.Value.Session.StaleAfterSeconds));
    }

    /// <inheritdoc />
    public void Update(SessionLiveState state) => _sessions[state.WindowsSessionId] = state;

    /// <inheritdoc />
    public void RemoveState(int windowsSessionId) => _sessions.TryRemove(windowsSessionId, out _);

    /// <inheritdoc />
    public void MarkLoggedIn(int windowsSessionId, string? userName) => _loggedIn[windowsSessionId] = userName;

    /// <inheritdoc />
    public void Remove(int windowsSessionId)
    {
        _sessions.TryRemove(windowsSessionId, out _);
        _loggedIn.TryRemove(windowsSessionId, out _);
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionAgentInfo> GetSessionAgents()
    {
        var list = new List<SessionAgentInfo>();
        foreach (var (sid, user) in _loggedIn)
        {
            var has = _sessions.TryGetValue(sid, out var s);
            list.Add(new SessionAgentInfo(sid, s?.UserName ?? user, has && IsFresh(s!), has ? s!.ReportedUtc : null));
        }
        foreach (var s in _sessions.Values.Where(s => !_loggedIn.ContainsKey(s.WindowsSessionId)))
            list.Add(new SessionAgentInfo(s.WindowsSessionId, s.UserName, IsFresh(s), s.ReportedUtc));
        return list;
    }

    /// <inheritdoc />
    public LiveState? BuildLiveState(double? cpuPercent, double? memoryUsedPercent, bool? networkConnected)
    {
        var fresh = _sessions.Values.Where(IsFresh).OrderBy(s => PresenceRank(s.Presence)).ThenByDescending(s => s.ReportedUtc).FirstOrDefault();
        if (fresh is null)
            return new LiveState(Presence.Unknown, null, null, null, [], 0, false, null, cpuPercent, memoryUsedPercent, networkConnected);
        return new LiveState(fresh.Presence, fresh.UserName, fresh.WindowsSessionId, fresh.Foreground, fresh.OpenApplications,
            fresh.IdleSeconds, fresh.Interacting, fresh.LastInputUtc, cpuPercent, memoryUsedPercent, networkConnected);
    }

    private bool IsFresh(SessionLiveState s) => _time.GetUtcNow() - s.ReportedUtc <= _stale;

    private static int PresenceRank(string presence) => presence switch { Presence.Active => 0, Presence.Idle => 1, Presence.Locked => 2, _ => 3 };
}

/// <summary>Tracks collector health for /agent/status and diagnostics.</summary>
public sealed class CollectorRegistry : ICollectorRegistry
{
    private readonly ConcurrentDictionary<string, CollectorHealth> _items = new();
    private readonly TimeProvider _time;

    public CollectorRegistry(TimeProvider time) => _time = time;

    /// <inheritdoc />
    public void Report(string name, string state, string? error = null, bool restarted = false)
        => _items.AddOrUpdate(name,
            _ => new CollectorHealth(name, state, error, null, restarted ? 1 : 0),
            (_, old) => old with { State = state, LastError = error ?? (state == "Running" ? null : old.LastError), Restarts = old.Restarts + (restarted ? 1 : 0) });

    /// <inheritdoc />
    public void Touch(string name)
        => _items.AddOrUpdate(name, _ => new CollectorHealth(name, "Running", null, _time.GetUtcNow(), 0), (_, old) => old with { LastEventUtc = _time.GetUtcNow() });

    /// <inheritdoc />
    public IReadOnlyList<CollectorHealth> Snapshot() => _items.Values.OrderBy(i => i.Name).ToList();
}

/// <summary>V1 placeholder: screenshots are never taken.</summary>
public sealed class DisabledScreenCaptureService : IScreenCaptureService
{
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureMode mode, string reason, CancellationToken cancellationToken)
        => Task.FromResult(ScreenCaptureResult.Disabled);
}

/// <summary>V1 placeholder: browser monitoring is a future module.</summary>
public sealed class NotEnabledBrowserMonitoringProvider : IBrowserMonitoringProvider
{
    public bool IsEnabled => false;
    public string Status => "Not Enabled";
}
