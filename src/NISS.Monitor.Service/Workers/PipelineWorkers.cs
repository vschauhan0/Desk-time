using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Infrastructure.Runtime;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Service.Workers;

/// <summary>Drains the bounded collector channel into the durable SQLite queue in small transactions.</summary>
public sealed class EventPipelineWorker : BackgroundService
{
    private readonly ChannelEventSink _sink;
    private readonly IEventFactory _factory;
    private readonly IEventQueue _queue;
    private readonly AgentGate _gate;
    private readonly ILogger<EventPipelineWorker> _log;

    public EventPipelineWorker(ChannelEventSink sink, IEventFactory factory, IEventQueue queue, AgentGate gate, ILogger<EventPipelineWorker> log)
    {
        _sink = sink; _factory = factory; _queue = queue; _gate = gate; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _sink.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                await FlushAvailableAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally
        {
            // Persist whatever collectors managed to emit before shutdown.
            try { await FlushAvailableAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Final event flush failed"); }
        }
    }

    private async Task FlushAvailableAsync(CancellationToken ct)
    {
        var reader = _sink.Reader;
        var batch = new List<EventEnvelope>(200);
        while (reader.TryRead(out var draft))
        {
            if (_gate.Suspended) continue; // disabled/revoked by an administrator: collect nothing
            var env = await _factory.CreateAsync(draft, ct).ConfigureAwait(false);
            if (env is not null) batch.Add(env);
            if (batch.Count >= 200) { await PersistAsync(batch, ct).ConfigureAwait(false); batch.Clear(); }
        }
        await PersistAsync(batch, ct).ConfigureAwait(false);
    }

    private async Task PersistAsync(List<EventEnvelope> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        try { await _queue.EnqueueBatchAsync(batch, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _log.LogError(ex, "Could not persist {Count} events to the local queue", batch.Count);
        }
    }
}

/// <summary>Periodic SYSTEM_METRIC sampling.</summary>
public sealed class SystemMetricsWorker : BackgroundService
{
    private readonly ISystemMetricsCollector _collector;
    private readonly IEventSink _sink;
    private readonly IPolicyProvider _policy;
    private readonly LatestMetrics _latest;
    private readonly LocalTelemetryStore _local;
    private readonly DeviceIdentityStore _identity;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<SystemMetricsWorker> _log;

    public SystemMetricsWorker(ISystemMetricsCollector collector, IEventSink sink, IPolicyProvider policy, LatestMetrics latest, LocalTelemetryStore local,
        DeviceIdentityStore identity, ICollectorRegistry registry, TimeProvider time, ILogger<SystemMetricsWorker> log)
    {
        _collector = collector; _sink = sink; _policy = policy; _latest = latest; _local = local; _identity = identity; _registry = registry; _time = time; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string name = "SystemMetrics";
        _registry.Report(name, "Running");
        _collector.Collect(); // prime the CPU delta
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _policy.Current.MetricsSeconds)), stoppingToken).ConfigureAwait(false);
                try
                {
                    var m = _collector.Collect();
                    _latest.Value = m;
                    if (_identity.Current is null) continue;
                    var now = _time.GetUtcNow();
                    _local.RecordMetric(now, m);
                    await _sink.EmitAsync(EventDraft.Create(EventTypes.SystemMetric, EventSources.Service, now, m), stoppingToken).ConfigureAwait(false);
                    _registry.Touch(name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Metric sample failed");
                    _registry.Report(name, "Degraded", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Sends heartbeats on the policy interval; independent of the uploader so liveness never waits on a backlog.</summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private readonly IHeartbeatService _heartbeat;
    private readonly DeviceIdentityStore _identity;
    private readonly IPolicyProvider _policy;
    private readonly ICollectorRegistry _registry;
    private readonly ILogger<HeartbeatWorker> _log;

    public HeartbeatWorker(IHeartbeatService heartbeat, DeviceIdentityStore identity, IPolicyProvider policy, ICollectorRegistry registry, ILogger<HeartbeatWorker> log)
    {
        _heartbeat = heartbeat; _identity = identity; _policy = policy; _registry = registry; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_identity.Reload() is not null)
                {
                    var ok = await _heartbeat.SendAsync(stoppingToken).ConfigureAwait(false);
                    failures = ok ? 0 : failures + 1;
                    _registry.Report("Heartbeat", ok ? "Running" : "Degraded", ok ? null : "Heartbeat failing");
                    if (ok) _registry.Touch("Heartbeat");
                    else if (failures == 1 || failures % 10 == 0) _log.LogWarning("Heartbeat has failed {Count} time(s) in a row", failures);
                }
                var interval = Math.Max(5, _policy.Current.HeartbeatSeconds);
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Uploads the local queue whenever there is something to send; back-off is tracked per event in SQLite.</summary>
public sealed class UploadWorker : BackgroundService
{
    private readonly IUploader _uploader;
    private readonly DeviceIdentityStore _identity;
    private readonly IPolicyProvider _policy;
    private readonly ICollectorRegistry _registry;
    private readonly HeartbeatService _heartbeat;
    private readonly ILogger<UploadWorker> _log;

    public UploadWorker(IUploader uploader, DeviceIdentityStore identity, IPolicyProvider policy, ICollectorRegistry registry, HeartbeatService heartbeat, ILogger<UploadWorker> log)
    {
        _uploader = uploader; _identity = identity; _policy = policy; _registry = registry; _heartbeat = heartbeat; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasReachable = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_identity.Reload() is not null)
                {
                    var r = await _uploader.UploadPendingAsync(stoppingToken).ConfigureAwait(false);
                    if (r.Uploaded + r.Duplicates > 0) _log.LogInformation("Uploaded {Count} events ({Duplicates} already known to the server)", r.Uploaded + r.Duplicates, r.Duplicates);
                    if (r.ServerReachable && !wasReachable) _log.LogInformation("Server reachable again - backlog is draining");
                    wasReachable = r.ServerReachable;
                    _heartbeat.SetLastError(r.Error);
                    _registry.Report("Uploader", r.Error is null ? "Running" : "Degraded", r.Error);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _policy.Current.UploadIntervalSeconds)), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Posts the richer /agent/status report every few minutes (and after policy changes).</summary>
public sealed class AgentStatusWorker : BackgroundService
{
    private readonly ServerApiClient _api;
    private readonly DeviceIdentityStore _identity;
    private readonly IEventQueue _queue;
    private readonly ICollectorRegistry _registry;
    private readonly ILiveStateStore _live;
    private readonly LatestMetrics _metrics;
    private readonly MachineInfo _machine;
    private readonly ILogger<AgentStatusWorker> _log;

    public AgentStatusWorker(ServerApiClient api, DeviceIdentityStore identity, IEventQueue queue, ICollectorRegistry registry, ILiveStateStore live,
        LatestMetrics metrics, MachineInfo machine, ILogger<AgentStatusWorker> log)
    {
        _api = api; _identity = identity; _queue = queue; _registry = registry; _live = live; _metrics = metrics; _machine = machine; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
                var id = _identity.Current;
                if (id is null) continue;

                var stats = await _queue.GetStatsAsync(stoppingToken).ConfigureAwait(false);
                var m = _metrics.Value;
                var collectors = _registry.Snapshot().Select(c => new CollectorStatus(c.Name, c.State, c.LastError, c.LastEventUtc)).ToList();
                var request = new AgentStatusRequest(id.DeviceId, AgentVersion.Current, collectors, stats.Pending, stats.DeadLetters, stats.OldestPendingUtc,
                    stats.LastUploadUtc, stats.LastError, stats.LastError, true, _live.GetSessionAgents(), _machine.OsVersion, _machine.Hostname,
                    _machine.Manufacturer, _machine.Model, _machine.Serial, m?.CpuPercent, m?.MemoryUsedPercent, m?.Disks.Count > 0 ? m.Disks.Max(d => d.UsedPercent) : null);
                var r = await _api.PostStatusAsync(request, stoppingToken).ConfigureAwait(false);
                if (!r.Success) _log.LogDebug("Status report failed: {Error}", r.Error);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Housekeeping: prune uploaded events, enforce the pending cap.</summary>
public sealed class QueueMaintenanceWorker : BackgroundService
{
    private readonly IEventQueue _queue;
    private readonly AgentSettings _settings;
    private readonly ILogger<QueueMaintenanceWorker> _log;

    public QueueMaintenanceWorker(IEventQueue queue, IOptions<AgentSettings> settings, ILogger<QueueMaintenanceWorker> log)
    {
        _queue = queue; _settings = settings.Value; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken).ConfigureAwait(false);
                try { await _queue.MaintainAsync(TimeSpan.FromHours(_settings.Storage.KeepUploadedHours), _settings.Storage.MaxPendingEvents, stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { _log.LogWarning(ex, "Queue maintenance failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
