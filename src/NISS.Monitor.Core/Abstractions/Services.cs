using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Abstractions;

/// <summary>Durable local event queue (SQLite). Ordering is by <c>sequence</c>; ids are idempotency keys.</summary>
public interface IEventQueue
{
    /// <summary>Persist an event. Never blocks on the network.</summary>
    ValueTask EnqueueAsync(EventEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>Persist several events in one transaction (preserves the given order).</summary>
    Task EnqueueBatchAsync(IReadOnlyList<EventEnvelope> envelopes, CancellationToken cancellationToken);

    /// <summary>Oldest pending events in sequence order (skipping events still in retry back-off).</summary>
    Task<IReadOnlyList<QueuedEvent>> PeekBatchAsync(int max, CancellationToken cancellationToken);

    /// <summary>Remove events the server has stored (accepted or duplicate).</summary>
    Task MarkUploadedAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken);

    /// <summary>Move permanently rejected events out of the way so they cannot block the queue.</summary>
    Task MarkDeadLetterAsync(IReadOnlyCollection<(Guid EventId, string? Error)> events, CancellationToken cancellationToken);

    /// <summary>Record a failed attempt and delay the events (exponential back-off).</summary>
    Task MarkFailedAsync(IReadOnlyCollection<Guid> eventIds, string error, TimeSpan retryAfter, CancellationToken cancellationToken);

    /// <summary>Queue depth, oldest pending event, last upload/error.</summary>
    Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>Highest sequence number ever assigned (used to seed the sequence generator).</summary>
    Task<long> GetMaxSequenceAsync(CancellationToken cancellationToken);

    /// <summary>Housekeeping: prune uploaded events and enforce the pending cap.</summary>
    Task MaintainAsync(TimeSpan keepUploaded, int maxPending, CancellationToken cancellationToken);
}

/// <summary>Turns a draft into a complete envelope (identity, sequence, clock correction, local timestamp).</summary>
public interface IEventFactory
{
    /// <summary>Create the envelope. Returns null when the device is not enrolled yet.</summary>
    Task<EventEnvelope?> CreateAsync(EventDraft draft, CancellationToken cancellationToken);
}

/// <summary>Small key/value store in the agent database (agent_state table).</summary>
public interface IAgentStateStore
{
    /// <summary>Read a value.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Write a value.</summary>
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}

/// <summary>Effective monitoring policy (server policy over local defaults).</summary>
public interface IPolicyProvider
{
    /// <summary>The policy in force.</summary>
    AgentPolicy Current { get; }

    /// <summary>Raised when the policy changes.</summary>
    event Action<AgentPolicy>? Changed;

    /// <summary>Apply a new policy from the server and persist it.</summary>
    Task UpdateAsync(AgentPolicy policy, CancellationToken cancellationToken);
}

/// <summary>Latest state reported by session processes, merged for the heartbeat.</summary>
public interface ILiveStateStore
{
    /// <summary>Store a state report from a session process.</summary>
    void Update(SessionLiveState state);

    /// <summary>Forget a session's reported state (its session process disconnected).</summary>
    void RemoveState(int windowsSessionId);

    /// <summary>Record that a user is logged in to a Windows session (so a missing session process can be reported).</summary>
    void MarkLoggedIn(int windowsSessionId, string? userName);

    /// <summary>The user logged out: forget the session completely.</summary>
    void Remove(int windowsSessionId);

    /// <summary>Merged live state for the heartbeat (null user/presence when nobody is logged in).</summary>
    LiveState? BuildLiveState(double? cpuPercent, double? memoryUsedPercent, bool? networkConnected);

    /// <summary>Per-session session-agent info (connected / stale).</summary>
    IReadOnlyList<SessionAgentInfo> GetSessionAgents();
}

/// <summary>Registry the supervisor uses to publish collector health (feeds /agent/status and diagnostics).</summary>
public interface ICollectorRegistry
{
    /// <summary>Report the state of a collector.</summary>
    void Report(string name, string state, string? error = null, bool restarted = false);

    /// <summary>Mark that a collector produced something.</summary>
    void Touch(string name);

    /// <summary>Snapshot of all collectors.</summary>
    IReadOnlyList<CollectorHealth> Snapshot();
}
