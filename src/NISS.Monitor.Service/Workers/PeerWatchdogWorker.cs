using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;

namespace NISS.Monitor.Service.Workers;

/// <summary>
/// Runs inside the main service and watches its "buddy" NISS Monitor Watchdog service, restarting it if anyone
/// stops it (Task Manager, Services.msc, <c>sc stop</c>). The watchdog service does the same for this one - see
/// <see cref="PeerServiceWatchdog"/> for what this resilience does and does not do.
/// </summary>
public sealed class PeerWatchdogWorker : BackgroundService
{
    private readonly PeerServiceWatchdog _watchdog;
    private readonly ILogger<PeerWatchdogWorker> _log;
    private readonly ICollectorRegistry _registry;

    public PeerWatchdogWorker(IServiceStateController services, TimeProvider time, ICollectorRegistry registry, ILogger<PeerWatchdogWorker> log)
    {
        _log = log;
        _registry = registry;
        _watchdog = new PeerServiceWatchdog(services, time, AgentConstants.WatchdogServiceName, AgentConstants.WatchdogPollInterval);
        _watchdog.PeerWasStopped += elapsed =>
        {
            _log.LogWarning("NISS Monitor Watchdog service was found stopped and has been restarted (noticed within {Seconds:0.0}s)", elapsed.TotalSeconds);
            _registry.Report("PeerWatchdog", "Running", $"Restarted the watchdog service {_watchdog.RestartCount} time(s) so far");
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registry.Report("PeerWatchdog", "Running");
        try { await _watchdog.RunAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
