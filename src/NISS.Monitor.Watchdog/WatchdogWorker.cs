using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;

namespace NISS.Monitor.Watchdog;

/// <summary>Watches the main NISS Monitor service and restarts it if it is ever found stopped.</summary>
public sealed class WatchdogWorker : BackgroundService
{
    private readonly PeerServiceWatchdog _watchdog;
    private readonly ILogger<WatchdogWorker> _log;

    public WatchdogWorker(IServiceStateController services, TimeProvider time, ILogger<WatchdogWorker> log)
    {
        _log = log;
        _watchdog = new PeerServiceWatchdog(services, time, AgentConstants.ServiceName, AgentConstants.WatchdogPollInterval);
        _watchdog.PeerWasStopped += elapsed =>
            _log.LogWarning("NISS Monitor Service was found stopped and has been restarted (noticed within {Seconds:0.0}s)", elapsed.TotalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("NISS Monitor Watchdog started; polling every {Seconds}s", AgentConstants.WatchdogPollInterval.TotalSeconds);
        try { await _watchdog.RunAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _log.LogInformation("NISS Monitor Watchdog stopping (restarted the main service {Count} time(s) this run)", _watchdog.RestartCount);
    }
}
