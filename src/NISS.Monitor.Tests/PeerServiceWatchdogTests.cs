using Microsoft.Extensions.Time.Testing;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;

namespace NISS.Monitor.Tests;

/// <summary>A fake service registry so the mutual-watchdog decision logic is testable without any real Windows service.</summary>
public sealed class FakeServiceStateController : IServiceStateController
{
    private readonly Dictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);
    public int StartCalls { get; private set; }

    public void SetRunning(string serviceName, bool running) => _running[serviceName] = running;

    public bool IsRunning(string serviceName) => _running.TryGetValue(serviceName, out var r) && r;

    public void StartIfStopped(string serviceName)
    {
        StartCalls++;
        if (!IsRunning(serviceName)) _running[serviceName] = true; // a real Start() would bring it up
    }
}

public class PeerServiceWatchdogTests
{
    [Fact]
    public void Running_peer_is_left_alone()
    {
        var services = new FakeServiceStateController();
        services.SetRunning("Peer", true);
        var wd = new PeerServiceWatchdog(services, new FakeTimeProvider(), "Peer", TimeSpan.FromSeconds(5));

        wd.Tick();

        Assert.Equal(0, services.StartCalls);
        Assert.Equal(0, wd.RestartCount);
    }

    [Fact]
    public void Stopped_peer_is_started_and_reported()
    {
        var services = new FakeServiceStateController();
        services.SetRunning("Peer", false);
        var wd = new PeerServiceWatchdog(services, new FakeTimeProvider(), "Peer", TimeSpan.FromSeconds(5));
        TimeSpan? reported = null;
        wd.PeerWasStopped += t => reported = t;

        wd.Tick();

        Assert.Equal(1, services.StartCalls);
        Assert.Equal(1, wd.RestartCount);
        Assert.NotNull(reported);
        Assert.True(services.IsRunning("Peer"));
    }

    [Fact]
    public void Already_running_peer_does_not_raise_the_restarted_event()
    {
        var services = new FakeServiceStateController();
        services.SetRunning("Peer", true);
        var wd = new PeerServiceWatchdog(services, new FakeTimeProvider(), "Peer", TimeSpan.FromSeconds(5));
        var raised = false;
        wd.PeerWasStopped += _ => raised = true;

        wd.Tick();
        wd.Tick();

        Assert.False(raised);
    }

    [Fact]
    public async Task Killed_peer_is_restarted_within_one_poll_interval_well_under_15_seconds()
    {
        // Mirrors the real scenario: someone stops the peer service; the watchdog notices on its next poll.
        var services = new FakeServiceStateController();
        services.SetRunning("Peer", true);
        var time = new FakeTimeProvider();
        var wd = new PeerServiceWatchdog(services, time, "Peer", TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var run = wd.RunAsync(cts.Token);

        services.SetRunning("Peer", false); // simulate Task Manager "End Task"
        time.Advance(TimeSpan.FromSeconds(5)); // one poll interval - well within the 15s requirement
        await Task.Delay(50); // let the PeriodicTimer tick complete

        Assert.True(services.IsRunning("Peer"));
        Assert.Equal(1, wd.RestartCount);
        await cts.CancelAsync();
        await Task.WhenAny(run, Task.Delay(1000));
    }

    [Fact]
    public void Repeated_kills_are_repeatedly_corrected()
    {
        var services = new FakeServiceStateController();
        services.SetRunning("Peer", true);
        var wd = new PeerServiceWatchdog(services, new FakeTimeProvider(), "Peer", TimeSpan.FromSeconds(5));

        for (var i = 0; i < 5; i++)
        {
            services.SetRunning("Peer", false);
            wd.Tick();
            Assert.True(services.IsRunning("Peer"));
        }
        Assert.Equal(5, wd.RestartCount);
    }

    [Fact]
    public void Missing_service_never_throws()
    {
        var services = new FakeServiceStateController(); // "Peer" never registered at all
        var wd = new PeerServiceWatchdog(services, new FakeTimeProvider(), "Peer", TimeSpan.FromSeconds(5));
        wd.Tick(); // must not throw
        Assert.Equal(1, wd.RestartCount);
    }
}
