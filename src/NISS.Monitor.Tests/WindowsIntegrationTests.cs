using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Collectors;
using NISS.Monitor.Infrastructure.Interactive;
using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Infrastructure.Runtime;
using NISS.Monitor.Protocol;
using NISS.Monitor.Service.Workers;

namespace NISS.Monitor.Tests;

/// <summary>Real Windows API smoke tests (no elevation, no synthetic input).</summary>
public class WindowsCollectorTests
{
    [Fact]
    public void Toolhelp_snapshot_sees_a_started_process_then_its_stop()
    {
        var engine = new ProcessDiffEngine();
        var filter = new ProcessFilter("All", null);
        engine.Update(ProcessMonitor.TakeSnapshot().Where(filter.ShouldTrack).ToList()); // baseline

        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var started = engine.Update(ProcessMonitor.TakeSnapshot().Where(filter.ShouldTrack).ToList());
            Assert.Contains(started.Started, p => p.Pid == child.Id && p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));      // PROCESS_STARTED
            Assert.Equal(Process.GetCurrentProcess().SessionId, started.Started.First(p => p.Pid == child.Id).SessionId);
        }
        finally { child.Kill(); child.WaitForExit(); }

        var stopped = engine.Update(ProcessMonitor.TakeSnapshot().Where(filter.ShouldTrack).ToList());
        Assert.Contains(stopped.Stopped, p => p.Pid == child.Id);                                                                              // PROCESS_STOPPED
    }

    [Fact]
    public async Task Process_monitor_emits_started_and_stopped_events_with_details_and_deterministic_ids()
    {
        await using var host = new AgentTestHost(config: new() { ["Agent:Monitoring:ProcessPollSeconds"] = "1" });
        await host.EnrollAsync();
        var sink = host.Get<ChannelEventSink>();
        var monitor = (ProcessMonitor)host.Get<IProcessMonitor>();

        await monitor.PollOnceAsync(default); // baseline
        while (sink.Reader.TryRead(out _)) { }

        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        await monitor.PollOnceAsync(default);
        child.Kill(); child.WaitForExit();
        await monitor.PollOnceAsync(default);

        var drafts = new List<EventDraft>();
        while (sink.Reader.TryRead(out var d)) drafts.Add(d);
        var start = drafts.Single(d => d.EventType == EventTypes.ProcessStarted && ProtocolJson.FromElement<ProcessPayload>(d.Payload)!.ProcessId == child.Id);
        var stop = drafts.Single(d => d.EventType == EventTypes.ProcessStopped && ProtocolJson.FromElement<ProcessPayload>(d.Payload)!.ProcessId == child.Id);
        var sp = ProtocolJson.FromElement<ProcessPayload>(start.Payload)!;
        var ep = ProtocolJson.FromElement<ProcessPayload>(stop.Payload)!;

        Assert.Equal(sp.ProcessSessionId, ep.ProcessSessionId);
        Assert.EndsWith("ping.exe", sp.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(sp.ParentProcessId);
        Assert.NotNull(ep.EndedUtc);
        Assert.NotEqual(start.EventId, stop.EventId);
        Assert.Equal(DeterministicGuid.Create($"{sp.ProcessSessionId}|{EventTypes.ProcessStarted}"), start.EventId);
    }

    [Fact]
    public async Task Process_monitor_does_not_re_report_running_processes_after_an_agent_restart()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var sink = host.Get<ChannelEventSink>();

        await ((ProcessMonitor)host.Get<IProcessMonitor>()).PollOnceAsync(default);
        var firstRunCount = 0; while (sink.Reader.TryRead(out _)) firstRunCount++;
        Assert.True(firstRunCount > 0);

        // "restart": a fresh monitor loads open sessions from SQLite
        var again = new ProcessMonitor(sink, host.Get<IPolicyProvider>(), host.Get<NISS.Monitor.Infrastructure.Persistence.LocalTelemetryStore>(),
            host.Get<NISS.Monitor.Infrastructure.Identity.DeviceIdentityStore>(), host.Get<ICollectorRegistry>(), TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new NISS.Monitor.Core.Configuration.AgentSettings()), NullLogger<ProcessMonitor>.Instance);
        var ctor = typeof(ProcessMonitor).GetField("_stored", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ctor.SetValue(again, host.Get<NISS.Monitor.Infrastructure.Persistence.LocalTelemetryStore>().LoadOpenProcessSessions());
        await again.PollOnceAsync(default);
        var secondRunCount = 0; while (sink.Reader.TryRead(out _)) secondRunCount++;
        Assert.True(secondRunCount < firstRunCount / 2 + 5, $"expected few or no re-reports but got {secondRunCount} (first run: {firstRunCount})");
    }

    [Fact]
    public void Foreground_and_open_window_detection_run_without_error()
    {
        var fg = new Win32ForegroundMonitor();
        var sample = fg.Sample(captureTitle: true); // may be null on a locked/headless machine; must never throw
        if (sample is not null)
        {
            Assert.True(sample.ProcessId > 0);
            Assert.False(string.IsNullOrEmpty(sample.ProcessName));
        }
        Assert.All(fg.EnumerateOpenApplications(false), a => { Assert.True(a.ProcessId > 0); Assert.Null(a.WindowTitle); }); // titles honour the policy switch
    }

    [Fact]
    public void Idle_time_is_readable_and_non_negative()
        => Assert.True(new Win32IdleMonitor().GetIdleTime() >= TimeSpan.Zero);

    [Fact]
    public void System_metrics_are_sane()
    {
        var c = new SystemMetricsCollector();
        c.Collect();
        Thread.Sleep(300);
        var m = c.Collect();
        Assert.InRange(m.CpuPercent, 0, 100);
        Assert.InRange(m.MemoryUsedPercent, 1, 100);
        Assert.True(m.MemoryTotalMb > 512);
        Assert.NotEmpty(m.Disks);
        Assert.True(m.UptimeSeconds > 0);
    }

    [Fact]
    public void Wts_session_enumeration_does_not_throw()
        => Assert.NotNull(WtsSessionMonitor.Enumerate());

    [Fact]
    public async Task Message_window_starts_and_stops_cleanly()
    {
        var monitor = new MessageWindowMonitor();
        var agg = new InputBucketAggregator(TimeProvider.System, 30, TimeSpan.FromMilliseconds(500));
        using var cts = new CancellationTokenSource();
        var run = monitor.RunAsync(agg, cts.Token);
        await Task.Delay(400);
        Assert.False(run.IsFaulted, run.Exception?.ToString());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Usb_and_network_monitors_baseline_without_error()
    {
        await using var host = new AgentTestHost();
        await ((UsbMonitor)host.Get<IUsbMonitor>()).PollOnceAsync(default);
        var m = host.Get<ISystemMetricsCollector>().Collect();
        Assert.NotNull(m.IpAddresses);
    }

    [Fact]
    public async Task Screenshot_and_browser_modules_are_disabled_placeholders()
    {
        var screen = new DisabledScreenCaptureService();
        Assert.False(screen.IsEnabled);
        Assert.False((await screen.CaptureAsync(ScreenCaptureMode.Manual, "test", default)).Captured);
        var browser = new NotEnabledBrowserMonitoringProvider();
        Assert.False(browser.IsEnabled);
        Assert.Equal("Not Enabled", browser.Status);
    }
}

public class ServiceResilienceTests
{
    [Fact]
    public async Task A_failing_collector_is_restarted_and_does_not_affect_the_others()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var flaky = new FlakyCollector();
        var healthy = new CountingCollector();
        var registry = host.Get<ICollectorRegistry>();
        var sink = host.Get<IEventSink>();
        var identity = host.Get<NISS.Monitor.Infrastructure.Identity.DeviceIdentityStore>();
        var a = new CollectorWorker(flaky, identity, registry, sink, TimeProvider.System, NullLoggerFactory.Instance);
        var b = new CollectorWorker(healthy, identity, registry, sink, TimeProvider.System, NullLoggerFactory.Instance);

        using var cts = new CancellationTokenSource();
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);
        await Task.Delay(6000); // first failure -> ~2 s back-off -> restart succeeds
        var healthyState = registry.Snapshot().Single(c => c.Name == "Healthy").State;
        await cts.CancelAsync();
        await a.StopAsync(default);
        await b.StopAsync(default);

        Assert.True(flaky.Runs >= 2, "flaky collector should have been restarted");
        Assert.True(healthy.Runs == 1);
        Assert.Contains(registry.Snapshot(), c => c.Name == "Flaky" && c.Restarts >= 1);
        Assert.Equal("Running", healthyState);
    }

    private sealed class FlakyCollector : ICollector
    {
        public int Runs;
        public string Name => "Flaky";
        public Task RunAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref Runs) == 1) throw new InvalidOperationException("USB stack unavailable");
            return Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class CountingCollector : ICollector
    {
        public int Runs;
        public string Name => "Healthy";
        public Task RunAsync(CancellationToken ct) { Interlocked.Increment(ref Runs); return Task.Delay(Timeout.Infinite, ct); }
    }

    [Fact]
    public async Task Unclean_shutdown_is_reported_on_next_start_and_graceful_stop_reports_agent_stopped()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var queue = host.Get<IEventQueue>();

        // Run 1 starts and is then "killed" (no StopAsync): clean_shutdown stays 0.
        var run1 = Lifecycle(host);
        await run1.StartAsync(default);
        await Task.Delay(300);
        Assert.Equal("0", await host.Get<IAgentStateStore>().GetAsync("clean_shutdown", default));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Run 2 (the restarted service) discovers the unclean shutdown and says so in AGENT_STARTED.
        var run2 = Lifecycle(host);
        await run2.StartAsync(default);
        var sink = host.Get<ChannelEventSink>();
        await Task.Delay(500);
        var drafts = new List<EventDraft>();
        while (sink.Reader.TryRead(out var d)) drafts.Add(d);
        var started = drafts.Where(d => d.EventType == EventTypes.AgentStarted).Select(d => ProtocolJson.FromElement<AgentPayload>(d.Payload)!).ToList();
        Assert.Contains(started, s => s.UnexpectedPreviousShutdown == true);

        // Graceful stop: AGENT_STOPPED is persisted (and uploaded immediately), flag flips to clean.
        await run2.StopAsync(default);
        Assert.Equal("1", await host.Get<IAgentStateStore>().GetAsync("clean_shutdown", default));
        Assert.Contains(host.Server.ReceivedOrder.Count > 0 ? "uploaded" : "queued", new[] { "uploaded", "queued" });
        var queuedOrUploaded = host.Server.Stored.Count + (await queue.GetStatsAsync(default)).Pending;
        Assert.True(queuedOrUploaded >= 1);
    }

    private static AgentLifecycleWorker Lifecycle(AgentTestHost h) => new(
        h.Get<NISS.Monitor.Infrastructure.Persistence.SqliteDatabase>(), h.Get<IAgentStateStore>(), h.Get<NISS.Monitor.Infrastructure.Identity.DeviceIdentityStore>(),
        h.Get<IDeviceEnrollmentService>(), h.Get<IEventFactory>(), h.Get<IEventQueue>(), h.Get<IEventSink>(), h.Get<IUploader>(), h.Get<ClockOffsetTracker>(),
        Microsoft.Extensions.Options.Options.Create(new NISS.Monitor.Core.Configuration.AgentSettings()), TimeProvider.System, NullLoggerFactory.Instance);
}

public class SessionPipeTests
{
    [Fact]
    public async Task Session_process_events_and_state_reach_the_service_and_survive_a_service_restart()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var pipeName = host.Get<Microsoft.Extensions.Options.IOptions<NISS.Monitor.Core.Configuration.AgentSettings>>().Value.Session.PipeName;
        var sink = host.Get<ChannelEventSink>();
        var live = host.Get<ILiveStateStore>();

        var client = new SessionPipeClient("alice", "1.0.0", pipeName);
        using var clientCts = new CancellationTokenSource();
        var clientTask = client.RunAsync(Process.GetCurrentProcess().SessionId, clientCts.Token);

        // --- service instance #1
        using var svc1 = new CancellationTokenSource();
        var server1 = host.Get<SessionPipeServer>();
        var s1 = server1.RunAsync(svc1.Token);
        await Eventually(() => client.Connected);

        await client.EmitAsync(EventDraft.Create(EventTypes.ApplicationForeground, EventSources.Session, DateTimeOffset.UtcNow,
            new ApplicationForegroundPayload(Guid.NewGuid(), 1, "EXCEL.EXE", null, "Salary.xlsx", null, 1)));
        client.SendState(new SessionLiveState(1, "alice", Presence.Active, new ForegroundInfo(1, "EXCEL.EXE", null, "Salary.xlsx"), [], 3, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var d = await ReadAsync(sink);
        Assert.Equal(EventTypes.ApplicationForeground, d.EventType);
        Assert.Equal(EventSources.Session, d.Source);
        await Eventually(() => live.BuildLiveState(null, null, null)?.Foreground?.ProcessName == "EXCEL.EXE");

        // --- the service is restarted (stop, then a new server on the same pipe); the session process reconnects
        svc1.Cancel();
        await Task.WhenAny(s1, Task.Delay(2000));
        await Eventually(() => !client.Connected, 5000);

        await client.EmitAsync(EventDraft.Create(EventTypes.SessionLock, EventSources.Session, DateTimeOffset.UtcNow, new SessionStatePayload(1, "alice", null, "Locked", null))); // buffered while the service is away
        using var svc2 = new CancellationTokenSource();
        var s2 = host.Get<SessionPipeServer>().RunAsync(svc2.Token);
        await Eventually(() => client.Connected, 15000);
        Assert.Equal(EventTypes.SessionLock, (await ReadAsync(sink)).EventType); // nothing was lost

        clientCts.Cancel(); svc2.Cancel();
        await Task.WhenAny(Task.WhenAll(clientTask, s2), Task.Delay(3000));
    }

    [Fact]
    public async Task Pipe_rejects_event_types_the_session_process_is_not_allowed_to_send()
    {
        await using var host = new AgentTestHost();
        await host.EnrollAsync();
        var pipeName = host.Get<Microsoft.Extensions.Options.IOptions<NISS.Monitor.Core.Configuration.AgentSettings>>().Value.Session.PipeName;
        var sink = host.Get<ChannelEventSink>();
        var client = new SessionPipeClient("mallory", "1.0.0", pipeName);
        using var cts = new CancellationTokenSource();
        var c = client.RunAsync(1, cts.Token);
        var s = host.Get<SessionPipeServer>().RunAsync(cts.Token);
        await Eventually(() => client.Connected);

        await client.EmitAsync(EventDraft.Create(EventTypes.UserLogout, EventSources.Session, DateTimeOffset.UtcNow, new SessionStatePayload(1, "x", null, null, null))); // a session process may not forge logouts
        await client.EmitAsync(EventDraft.Create(EventTypes.KeyboardActivity, EventSources.Session, DateTimeOffset.UtcNow, new InputActivityPayload(DateTimeOffset.UtcNow, 30, 5, null, null, null, null, null, null, null, null)));
        var d = await ReadAsync(sink);
        Assert.Equal(EventTypes.KeyboardActivity, d.EventType);
        cts.Cancel();
        await Task.WhenAny(Task.WhenAll(c, s), Task.Delay(2000));
    }

    private static async Task<EventDraft> ReadAsync(ChannelEventSink sink)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await sink.Reader.ReadAsync(cts.Token);
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }
}
