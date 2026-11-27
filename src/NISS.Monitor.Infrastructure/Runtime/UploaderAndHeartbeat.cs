using System.Text.Json;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Runtime;

/// <summary>Cross-component switches driven by the server (device disabled / revoked).</summary>
public sealed class AgentGate
{
    private volatile bool _suspended;
    private volatile string? _reason;

    /// <summary>True when the server disabled/revoked this device: stop uploading and drop new events.</summary>
    public bool Suspended => _suspended;

    /// <summary>Why collection is suspended.</summary>
    public string? Reason => _reason;

    public void Suspend(string reason) { _reason = reason; _suspended = true; }
    public void Resume() { _suspended = false; _reason = null; }
}

/// <summary>Latest metric sample, shared with the heartbeat and status reports.</summary>
public sealed class LatestMetrics
{
    private volatile SystemMetricPayload? _value;

    public SystemMetricPayload? Value { get => _value; set => _value = value; }
}

/// <summary>
/// Uploads the queue in sequence order. Network failures back off exponentially and never lose data; permanently
/// rejected events are dead-lettered; duplicates are safe because the server ingests idempotently on EventId.
/// </summary>
public sealed class Uploader : IUploader
{
    private const int MaxBatchesPerPass = 100;
    private readonly IEventQueue _queue;
    private readonly ServerApiClient _api;
    private readonly DeviceIdentityStore _identity;
    private readonly IPolicyProvider _policy;
    private readonly AgentGate _gate;
    private readonly ILogger<Uploader> _log;
    private readonly BackoffPolicy _backoff = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5));

    public Uploader(IEventQueue queue, ServerApiClient api, DeviceIdentityStore identity, IPolicyProvider policy, AgentGate gate, ILogger<Uploader> log)
    {
        _queue = queue;
        _api = api;
        _identity = identity;
        _policy = policy;
        _gate = gate;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<UploadResult> UploadPendingAsync(CancellationToken cancellationToken)
    {
        var id = _identity.Current;
        if (id is null) return new UploadResult(0, 0, 0, false, "Device is not enrolled.", false);
        if (_gate.Suspended) return new UploadResult(0, 0, 0, true, $"Uploads suspended: {_gate.Reason}", true);

        int uploaded = 0, duplicates = 0, dead = 0;
        for (var pass = 0; pass < MaxBatchesPerPass; pass++)
        {
            var batch = await _queue.PeekBatchAsync(Math.Clamp(_policy.Current.UploadBatchSize, 1, 500), cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0) break;

            var outcome = await SendAsync(id.DeviceId, batch, cancellationToken).ConfigureAwait(false);
            uploaded += outcome.Uploaded; duplicates += outcome.Duplicates; dead += outcome.DeadLettered;
            if (outcome.Stop) return new UploadResult(uploaded, duplicates, dead, outcome.Reachable, outcome.Error, outcome.AuthFailure);
        }
        return new UploadResult(uploaded, duplicates, dead, true, null, false);
    }

    private sealed record Outcome(int Uploaded, int Duplicates, int DeadLettered, bool Stop, bool Reachable, string? Error, bool AuthFailure);

    private async Task<Outcome> SendAsync(string deviceId, IReadOnlyList<QueuedEvent> batch, CancellationToken ct)
    {
        var envelopes = new List<EventEnvelope>(batch.Count);
        var unreadable = new List<(Guid, string?)>();
        foreach (var q in batch)
        {
            try
            {
                var env = JsonSerializer.Deserialize<EventEnvelope>(q.EnvelopeJson, ProtocolJson.Options);
                if (env is null) unreadable.Add((q.EventId, "null envelope")); else envelopes.Add(env);
            }
            catch (JsonException ex) { unreadable.Add((q.EventId, ex.Message)); }
        }
        if (unreadable.Count > 0) await _queue.MarkDeadLetterAsync(unreadable, ct).ConfigureAwait(false);
        if (envelopes.Count == 0) return new Outcome(0, 0, unreadable.Count, false, true, null, false);

        var result = await _api.PostEventsAsync(new EventBatchRequest(deviceId, envelopes), ct).ConfigureAwait(false);
        var ids = envelopes.Select(e => e.EventId).ToList();

        if (result.Success && result.Value is { } resp)
        {
            var stored = resp.Results.Where(r => r.Status is EventResultStatus.Accepted or EventResultStatus.Duplicate).Select(r => r.EventId).ToHashSet();
            var rejected = resp.Results.Where(r => r.Status == EventResultStatus.Rejected).Select(r => (r.EventId, r.Error)).ToList();
            var answered = new HashSet<Guid>(resp.Results.Select(r => r.EventId));
            var missing = ids.Where(i => !answered.Contains(i)).ToList();

            await _queue.MarkUploadedAsync(stored, ct).ConfigureAwait(false);
            await _queue.MarkDeadLetterAsync(rejected, ct).ConfigureAwait(false);
            if (missing.Count > 0)
                await _queue.MarkFailedAsync(missing, "Server response omitted the event.", _backoff.Next(1), ct).ConfigureAwait(false);

            _log.LogDebug("Uploaded {Accepted} accepted, {Duplicates} duplicate, {Rejected} rejected", resp.Accepted, resp.Duplicates, resp.Rejected);
            return new Outcome(resp.Accepted, resp.Duplicates, rejected.Count + unreadable.Count, missing.Count > 0, true, missing.Count > 0 ? "Incomplete server response" : null, false);
        }

        if (result.IsAuthFailure)
        {
            await _queue.MarkFailedAsync(ids, result.Error ?? "Authentication failed", TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            _log.LogError("Server rejected the device credential ({Status}). Uploads will retry every 5 minutes", result.StatusCode);
            return new Outcome(0, 0, 0, true, true, result.Error, true);
        }

        if (result.IsPermanentClientError)
        {
            if (envelopes.Count == 1)
            {
                await _queue.MarkDeadLetterAsync([(ids[0], result.Error)], ct).ConfigureAwait(false);
                _log.LogWarning("Event {EventId} permanently rejected: {Error}", ids[0], result.Error);
                return new Outcome(0, 0, 1, false, true, null, false);
            }
            // Isolate the poison event: send one by one this pass.
            int up = 0, du = 0, de = 0;
            foreach (var q in batch)
            {
                var single = await SendAsync(deviceId, [q], ct).ConfigureAwait(false);
                up += single.Uploaded; du += single.Duplicates; de += single.DeadLettered;
                if (single.Stop) return new Outcome(up, du, de, true, single.Reachable, single.Error, single.AuthFailure);
            }
            return new Outcome(up, du, de, false, true, null, false);
        }

        var attempt = batch.Max(b => b.Attempts);
        var delay = _backoff.Next(attempt);
        await _queue.MarkFailedAsync(ids, result.Error ?? "Upload failed", delay, ct).ConfigureAwait(false);
        _log.LogWarning("Upload failed ({Error}); retrying in {Delay:0}s. {Count} events remain queued", result.Error, delay.TotalSeconds, batch.Count);
        return new Outcome(0, 0, 0, true, result.Reachable, result.Error, false);
    }
}

/// <summary>Heartbeat: liveness, clock-offset measurement, live state, and policy-change detection.</summary>
public sealed class HeartbeatService : IHeartbeatService
{
    private readonly ServerApiClient _api;
    private readonly DeviceIdentityStore _identity;
    private readonly IEventQueue _queue;
    private readonly ILiveStateStore _live;
    private readonly LatestMetrics _metrics;
    private readonly ClockOffsetTracker _clock;
    private readonly IPolicyProvider _policy;
    private readonly IAgentStateStore _state;
    private readonly LocalTelemetryStore _local;
    private readonly AgentGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<HeartbeatService> _log;
    private readonly DateTimeOffset _startedUtc;
    private volatile string? _lastError;

    public HeartbeatService(ServerApiClient api, DeviceIdentityStore identity, IEventQueue queue, ILiveStateStore live, LatestMetrics metrics,
        ClockOffsetTracker clock, IPolicyProvider policy, IAgentStateStore state, LocalTelemetryStore local, AgentGate gate, TimeProvider time,
        ILogger<HeartbeatService> log)
    {
        _api = api; _identity = identity; _queue = queue; _live = live; _metrics = metrics; _clock = clock; _policy = policy;
        _state = state; _local = local; _gate = gate; _time = time; _log = log;
        _startedUtc = time.GetUtcNow();
    }

    /// <summary>Last error reported in heartbeats (upload failures etc.).</summary>
    public void SetLastError(string? error) => _lastError = error;

    /// <inheritdoc />
    public async Task<bool> SendAsync(CancellationToken cancellationToken)
    {
        var id = _identity.Current;
        if (id is null) return false;

        var stats = await _queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var m = _metrics.Value;
        var live = _live.BuildLiveState(m?.CpuPercent, m?.MemoryUsedPercent, m?.NetworkConnected);
        var uptime = (long)TimeSpan.FromMilliseconds(Native.NativeMethods.GetTickCount64()).TotalSeconds;

        var request = new HeartbeatRequest(id.DeviceId, AgentVersion.Current, _time.GetUtcNow(), uptime, "Running",
            _live.GetSessionAgents(), stats.Pending, stats.OldestPendingUtc, stats.LastUploadUtc, _lastError ?? stats.LastError,
            _clock.OffsetMs, _policy.Current.Version, live);

        var result = await _api.HeartbeatAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
        {
            _local.RecordHeartbeat(result.SentUtc, false, (int?)result.StatusCode, result.Error, (long)result.RoundTrip.TotalMilliseconds, null, _clock.OffsetMs);
            _log.LogWarning("Heartbeat failed: {Error}", result.Error);
            return false;
        }

        var resp = result.Value;
        _clock.AddSample(result.SentUtc, result.ReceivedUtc, resp.ServerTimeUtc);
        await _state.SetAsync("clock_offset_ms", _clock.OffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        _local.RecordHeartbeat(result.SentUtc, true, (int?)result.StatusCode, null, (long)result.RoundTrip.TotalMilliseconds, resp.ServerTimeUtc, _clock.OffsetMs);

        // Hand the update hint + liveness proof to the watchdog (which owns the actual update; see UpdateWorker).
        await RecordUpdateHintAsync(resp, cancellationToken).ConfigureAwait(false);

        if (resp.DeviceStatus is "DISABLED" or "REVOKED") _gate.Suspend($"Device {resp.DeviceStatus.ToLowerInvariant()} by administrator");
        else if (_gate.Suspended) _gate.Resume();

        if (resp.ConfigVersion > _policy.Current.Version)
        {
            var cfg = await _api.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            if (cfg.Success && cfg.Value is not null) await _policy.UpdateAsync(cfg.Value.Policy, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async Task RecordUpdateHintAsync(HeartbeatResponse resp, CancellationToken ct)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            await _state.SetAsync(AgentStateKeys.RunningVersion, AgentVersion.Current, ct).ConfigureAwait(false);
            await _state.SetAsync(AgentStateKeys.HeartbeatOkUtc, now, ct).ConfigureAwait(false);
            await _state.SetAsync(AgentStateKeys.UpdateTarget, resp.TargetAgentVersion ?? "", ct).ConfigureAwait(false);
            await _state.SetAsync(AgentStateKeys.UpdateUrl, resp.UpdateUrl ?? "", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _log.LogDebug(ex, "Could not record the update hint");
        }
    }
}

/// <summary>agent_state keys shared between the service (writer) and the watchdog (reader).</summary>
public static class AgentStateKeys
{
    public const string RunningVersion = "agent_running_version";
    public const string HeartbeatOkUtc = "last_heartbeat_ok_utc";
    public const string UpdateTarget = "update_target_version";
    public const string UpdateUrl = "update_url";
}
