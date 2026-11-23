using NISS.Monitor.Core.Abstractions;

namespace NISS.Monitor.Core.Engines;

/// <summary>
/// Watches ONE peer Windows service and starts it again if it is ever found stopped. Two of these, running inside
/// two independent services that each watch the other, mean stopping either one from Task Manager, Services.msc,
/// or <c>sc stop</c> is corrected automatically within one poll interval - without either process hiding itself,
/// blocking any admin tool, or doing anything a normal Windows service cannot do. An administrator who wants to
/// stop both permanently still can, by disabling both (or using the uninstaller); this defeats a single casual
/// stop, not a deliberate, informed action against both services.
/// </summary>
public sealed class PeerServiceWatchdog
{
    private readonly IServiceStateController _services;
    private readonly TimeProvider _time;
    private readonly string _peerServiceName;
    private readonly TimeSpan _pollInterval;

    public PeerServiceWatchdog(IServiceStateController services, TimeProvider time, string peerServiceName, TimeSpan pollInterval)
    {
        _services = services;
        _time = time;
        _peerServiceName = peerServiceName;
        _pollInterval = pollInterval;
    }

    /// <summary>Raised whenever a check found the peer stopped and asked it to start, with how long that check took to notice.</summary>
    public event Action<TimeSpan>? PeerWasStopped;

    /// <summary>Number of times the peer has been found stopped and restarted.</summary>
    public int RestartCount { get; private set; }

    /// <summary>Poll forever until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval, _time);
        Tick();
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            Tick();
    }

    /// <summary>One check-and-restart cycle (public for tests).</summary>
    public void Tick()
    {
        if (_services.IsRunning(_peerServiceName)) return;
        var start = _time.GetUtcNow();
        _services.StartIfStopped(_peerServiceName);
        RestartCount++;
        PeerWasStopped?.Invoke(_time.GetUtcNow() - start);
    }
}
