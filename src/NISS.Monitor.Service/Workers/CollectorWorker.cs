using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Service.Workers;

/// <summary>
/// Supervises one collector. If it throws it is restarted with exponential back-off; the failure is logged,
/// published to the collector registry and reported (throttled) as AGENT_ERROR. One broken collector (say USB) can
/// therefore never stop the others, the uploader, or the service itself.
/// </summary>
public sealed class CollectorWorker : BackgroundService
{
    private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromMinutes(5);
    private readonly ICollector _collector;
    private readonly DeviceIdentityStore _identity;
    private readonly ICollectorRegistry _registry;
    private readonly IEventSink _sink;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly BackoffPolicy _backoff = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2));

    public CollectorWorker(ICollector collector, DeviceIdentityStore identity, ICollectorRegistry registry, IEventSink sink, TimeProvider time, ILoggerFactory logs)
    {
        _collector = collector;
        _identity = identity;
        _registry = registry;
        _sink = sink;
        _time = time;
        _log = logs.CreateLogger($"NISS.Collector.{collector.Name}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registry.Report(_collector.Name, "WaitingForEnrollment");
        var attempt = 0;
        DateTimeOffset lastReport = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Collectors only run once the device has an identity: every event needs a DeviceId.
                while (_identity.Reload() is null)
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

                _log.LogInformation("Collector {Name} starting", _collector.Name);
                _registry.Report(_collector.Name, "Running", null, attempt > 0);
                await _collector.RunAsync(stoppingToken).ConfigureAwait(false);
                return; // returned normally: nothing more to do
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _registry.Report(_collector.Name, "Stopped");
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = _backoff.Next(attempt);
                _log.LogError(ex, "Collector {Name} failed (restart #{Attempt} in {Delay:0}s); other collectors are unaffected", _collector.Name, attempt, delay.TotalSeconds);
                _registry.Report(_collector.Name, "Failed", ex.Message, restarted: true);

                var now = _time.GetUtcNow();
                if (now - lastReport >= ErrorReportInterval)
                {
                    lastReport = now;
                    var payload = new AgentPayload(AgentVersion.Current, null, "COLLECTOR_FAILURE", Truncate(ex.Message), _collector.Name, null, null);
                    try { await _sink.EmitAsync(EventDraft.Create(EventTypes.AgentError, EventSources.Service, now, payload), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
