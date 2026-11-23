using Microsoft.Extensions.Time.Testing;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class ApplicationSessionEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 9, 0, 10, TimeSpan.Zero);
    private static ForegroundSample App(string name, int pid) => new(pid, name, $@"C:\apps\{name}", $"{name} window");

    /// <summary>Feed one sample per second between two instants.</summary>
    private static List<AppSessionChange> Run(ApplicationSessionEngine e, DateTimeOffset from, DateTimeOffset to, ForegroundSample? fg,
        bool locked = false, bool idle = false, bool interacting = true)
    {
        var all = new List<AppSessionChange>();
        for (var t = from; t <= to; t = t.AddSeconds(1)) all.AddRange(e.Tick(new AppTick(t, fg, locked, idle, interacting)));
        return all;
    }

    [Fact]
    public void Foreground_samples_become_sessions_not_rows_per_sample()
    {
        var e = new ApplicationSessionEngine();
        var excel = App("EXCEL.EXE", 10);
        var chrome = App("chrome.exe", 20);
        var outlook = App("OUTLOOK.EXE", 30);

        // Excel 09:00:10 -> 09:35:20, Chrome -> 09:52:10, Outlook -> 10:01:02 (the spec's example)
        var changes = new List<AppSessionChange>();
        changes.AddRange(Run(e, T0, T0.AddSeconds(2109), excel));                                   // 09:00:10 .. 09:35:19
        changes.AddRange(Run(e, T0.AddSeconds(2110), T0.AddSeconds(3119), chrome));                  // 09:35:20 .. 09:52:09
        changes.AddRange(Run(e, T0.AddSeconds(3120), T0.AddSeconds(3651), outlook));                 // 09:52:10 .. 10:01:01
        changes.AddRange(e.Tick(new AppTick(T0.AddSeconds(3652), excel, false, false, true)));       // 10:01:02 Excel again

        var ended = changes.Where(c => c.Kind == AppSessionChangeKind.Ended).Select(c => c.Session).ToList();
        Assert.Equal(3, ended.Count);
        Assert.Equal(new[] { "EXCEL.EXE", "chrome.exe", "OUTLOOK.EXE" }, ended.Select(s => s.ProcessName));
        Assert.Equal(TimeSpan.FromSeconds(2110), TimeSpan.FromSeconds(ended[0].DurationSeconds));    // 35m10s
        Assert.Equal(TimeSpan.FromSeconds(1010), TimeSpan.FromSeconds(ended[1].DurationSeconds));    // 16m50s
        Assert.Equal(TimeSpan.FromSeconds(532), TimeSpan.FromSeconds(ended[2].DurationSeconds));     // 8m52s
        Assert.Equal(4, changes.Count(c => c.Kind == AppSessionChangeKind.Started));                 // Excel, Chrome, Outlook, Excel
    }

    [Fact]
    public void Foreground_without_input_is_open_but_not_actively_used()
    {
        var e = new ApplicationSessionEngine();
        var chrome = App("chrome.exe", 20);
        Run(e, T0, T0.AddSeconds(60), chrome, interacting: true);                       // 60 s of real use
        Run(e, T0.AddSeconds(61), T0.AddSeconds(360), chrome, idle: true, interacting: false); // then idle in front
        var end = e.Flush(T0.AddSeconds(360), AppSessionEndReasons.Logout).Single().Session;

        Assert.Equal(360, end.DurationSeconds, 1);            // FOREGROUND time
        Assert.InRange(end.InteractionSeconds, 55, 65);       // ACTIVELY USED time is far smaller
        Assert.InRange(end.IdleSeconds, 295, 300);
    }

    [Fact]
    public void Lock_ends_the_session_with_reason_locked_and_unlock_starts_a_new_one()
    {
        var e = new ApplicationSessionEngine();
        var excel = App("EXCEL.EXE", 10);
        Run(e, T0, T0.AddSeconds(30), excel);
        var locked = e.Tick(new AppTick(T0.AddSeconds(31), null, true, false, false));
        Assert.Equal(AppSessionEndReasons.Locked, locked.Single().EndReason);
        Assert.Empty(e.Tick(new AppTick(T0.AddSeconds(32), excel, true, false, false))); // still locked: nothing in front
        Assert.Single(e.Tick(new AppTick(T0.AddSeconds(40), excel, false, false, true))); // unlock -> new session
    }

    [Fact]
    public void Large_gap_is_treated_as_suspend_and_does_not_inflate_durations()
    {
        var e = new ApplicationSessionEngine();
        var excel = App("EXCEL.EXE", 10);
        Run(e, T0, T0.AddSeconds(30), excel);
        var changes = e.Tick(new AppTick(T0.AddHours(8), excel, false, false, true)); // laptop slept 8 h
        var ended = changes.First(c => c.Kind == AppSessionChangeKind.Ended);
        Assert.Equal(AppSessionEndReasons.Suspended, ended.EndReason);
        Assert.Equal(30, ended.Session.DurationSeconds, 1);
    }

    [Fact]
    public void Title_change_in_same_process_does_not_split_the_session()
    {
        var e = new ApplicationSessionEngine();
        var changes = new List<AppSessionChange>();
        changes.AddRange(e.Tick(new AppTick(T0, new ForegroundSample(20, "chrome.exe", "p", "Tab A"), false, false, true)));
        changes.AddRange(e.Tick(new AppTick(T0.AddSeconds(1), new ForegroundSample(20, "chrome.exe", "p", "Tab B"), false, false, true)));
        Assert.Single(changes);
    }

    [Fact]
    public void Process_exit_ends_the_foreground_session()
    {
        var e = new ApplicationSessionEngine();
        e.Tick(new AppTick(T0, App("a.exe", 5), false, false, true));
        var ended = e.NotifyProcessExited(5, T0.AddSeconds(3)).Single();
        Assert.Equal(AppSessionEndReasons.ProcessExited, ended.EndReason);
    }
}

public class PresenceEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Active_then_idle_then_active_matches_the_spec_example()
    {
        var p = new PresenceEngine(TimeSpan.FromMinutes(5));
        Assert.Equal(EventTypes.ActiveStarted, p.Tick(T0, TimeSpan.Zero).Single().EventType);             // 09:00 active
        Assert.Empty(p.Tick(T0.AddMinutes(4), TimeSpan.FromMinutes(4)));                                   // still active
        var idle = p.Tick(T0.AddMinutes(5), TimeSpan.FromMinutes(5)).Single();                             // threshold reached
        Assert.Equal(EventTypes.IdleStarted, idle.EventType);
        Assert.Equal(T0, idle.IdleSinceUtc);                                                               // idle since the last real input, not since detection
        var back = p.Tick(T0.AddMinutes(12), TimeSpan.FromSeconds(1));                                      // 09:12 user returns
        Assert.Equal(new[] { EventTypes.IdleEnded, EventTypes.ActiveStarted }, back.Select(c => c.EventType));
        Assert.Equal(12 * 60, back[0].IdleSeconds!.Value, 1);
    }

    [Fact]
    public void Lock_while_idle_ends_the_idle_period_and_unlock_re_evaluates()
    {
        var p = new PresenceEngine(TimeSpan.FromMinutes(5));
        p.Tick(T0, TimeSpan.Zero);
        p.Tick(T0.AddMinutes(6), TimeSpan.FromMinutes(6));
        var locked = p.SetLocked(true, T0.AddMinutes(7));
        Assert.Equal(EventTypes.IdleEnded, locked.Single().EventType);
        Assert.Equal(PresenceState.Locked, p.State);
        Assert.Empty(p.Tick(T0.AddMinutes(8), TimeSpan.FromMinutes(8)));                                    // no idle events while locked
        p.SetLocked(false, T0.AddMinutes(9));
        Assert.Equal(EventTypes.ActiveStarted, p.Tick(T0.AddMinutes(9), TimeSpan.Zero).Single().EventType);
    }

    [Fact]
    public void Threshold_is_configurable_at_runtime()
    {
        var p = new PresenceEngine(TimeSpan.FromMinutes(5));
        p.Tick(T0, TimeSpan.Zero);
        p.SetThreshold(TimeSpan.FromMinutes(1));
        Assert.Equal(EventTypes.IdleStarted, p.Tick(T0.AddMinutes(2), TimeSpan.FromMinutes(2)).Single().EventType);
    }
}

public class ProcessEngineTests
{
    private static ProcessSnapshot P(int pid, string name, int ppid = 1, int session = 1) => new(pid, ppid, name, session);

    [Fact]
    public void First_snapshot_is_baseline_then_starts_and_stops_are_detected()
    {
        var d = new ProcessDiffEngine();
        var first = d.Update([P(100, "explorer.exe"), P(200, "chrome.exe")]);
        Assert.True(first.IsBaseline);
        Assert.Equal(2, first.Started.Count);

        var second = d.Update([P(100, "explorer.exe"), P(200, "chrome.exe"), P(300, "EXCEL.EXE")]);
        Assert.False(second.IsBaseline);
        Assert.Equal(300, second.Started.Single().Pid);   // PROCESS_STARTED
        Assert.Empty(second.Stopped);

        var third = d.Update([P(100, "explorer.exe"), P(300, "EXCEL.EXE")]);
        Assert.Equal(200, third.Stopped.Single().Pid);    // PROCESS_STOPPED
    }

    [Fact]
    public void Recycled_pid_is_reported_as_stop_then_start()
    {
        var d = new ProcessDiffEngine();
        d.Update([P(500, "a.exe")]);
        var diff = d.Update([P(500, "b.exe")]);
        Assert.Equal("a.exe", diff.Stopped.Single().Name);
        Assert.Equal("b.exe", diff.Started.Single().Name);
    }

    [Fact]
    public void Interactive_filter_drops_service_session_and_system_noise()
    {
        var f = new ProcessFilter("Interactive", ["myagent"]);
        Assert.True(f.ShouldTrack(P(10, "EXCEL.EXE")));
        Assert.False(f.ShouldTrack(P(11, "svchost.exe")));
        Assert.False(f.ShouldTrack(P(12, "worker.exe", session: 0)));
        Assert.False(f.ShouldTrack(P(13, "MyAgent.exe")));
        Assert.True(new ProcessFilter("All", null).ShouldTrack(P(14, "svchost.exe", session: 0)));
    }

    [Fact]
    public void Application_tracker_reports_open_and_close_of_window_owners()
    {
        var t = new ApplicationTracker();
        var (opened, closed) = t.Update([new OpenWindowApp(1, "excel.exe", null, "a"), new OpenWindowApp(2, "chrome.exe", null, "b")]);
        Assert.Equal(2, opened.Count);
        Assert.Empty(closed);
        (opened, closed) = t.Update([new OpenWindowApp(2, "chrome.exe", null, "b2")]);
        Assert.Empty(opened);
        Assert.Equal("excel.exe", closed.Single().ProcessName);
    }
}

public class SessionDiffEngineTests
{
    private static SessionDiffEngine.WtsSession S(int id, string state, string user = "alice", long logon = 1)
        => new(id, state, user, "NISS", null, DateTimeOffset.FromUnixTimeSeconds(logon));

    [Fact]
    public void Login_disconnect_reconnect_logout_are_detected()
    {
        var e = new SessionDiffEngine();
        Assert.Equal(SessionDiffEngine.Login, e.Update([S(2, "Active")]).Single().Kind);
        Assert.Empty(e.Update([S(2, "Active")]));
        Assert.Equal(SessionDiffEngine.Disconnect, e.Update([S(2, "Disconnected")]).Single().Kind);
        Assert.Equal(SessionDiffEngine.Connect, e.Update([S(2, "Active")]).Single().Kind);
        Assert.Equal(SessionDiffEngine.Logout, e.Update([]).Single().Kind);
    }

    [Fact]
    public void Same_session_id_with_new_logon_is_logout_then_login()
    {
        var e = new SessionDiffEngine();
        e.Update([S(2, "Active", "alice", 1)]);
        var kinds = e.Update([S(2, "Active", "bob", 2)]).Select(c => c.Kind).ToArray();
        Assert.Equal(new[] { SessionDiffEngine.Logout, SessionDiffEngine.Login }, kinds);
    }

    [Fact]
    public void Seeded_state_does_not_replay_logins_after_agent_restart()
    {
        var e = new SessionDiffEngine();
        e.Seed([S(2, "Active")]);
        Assert.Empty(e.Update([S(2, "Active")]));
        Assert.Equal(SessionDiffEngine.Logout, new SessionDiffEngine().Also(x => x.Seed([S(3, "Active")])).Update([]).Single().Kind); // logout while the agent was down
    }
}

internal static class Ext
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}

public class InputAggregatorTests
{
    [Fact]
    public void Counts_are_bucketed_and_only_counts_exist()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
        var agg = new InputBucketAggregator(time, 30, TimeSpan.FromMilliseconds(500));
        for (var i = 0; i < 12; i++) agg.RecordKeyDown();
        agg.RecordClick(10, 10);
        time.Advance(TimeSpan.FromMilliseconds(200));
        agg.RecordClick(11, 10);                 // double click (fast, same spot)
        agg.RecordWheel();
        agg.RecordMouseMove();

        Assert.Null(agg.TryFlush());             // bucket not elapsed yet
        time.Advance(TimeSpan.FromSeconds(31));
        var b = agg.TryFlush()!;
        Assert.Equal(12, b.KeyboardEvents);
        Assert.Equal(2, b.Clicks);
        Assert.Equal(1, b.DoubleClicks);
        Assert.Equal(1, b.WheelEvents);
        Assert.True(b.HasKeyboard && b.HasMouse);
        Assert.NotNull(b.LastKeyboardUtc);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(agg.TryFlush());             // empty buckets are not reported
    }

    [Fact]
    public void Keyboard_api_cannot_carry_key_identity()
    {
        // Privacy guard: the only keyboard entry point takes no arguments, so no character/scan code can be recorded.
        var m = typeof(InputBucketAggregator).GetMethod(nameof(InputBucketAggregator.RecordKeyDown))!;
        Assert.Empty(m.GetParameters());
        Assert.DoesNotContain(typeof(InputBucket).GetProperties(), p => p.PropertyType == typeof(string) || p.PropertyType == typeof(char) || p.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(typeof(InputActivityPayload).GetProperties(), p => p.PropertyType == typeof(string) || p.PropertyType == typeof(char) || p.PropertyType == typeof(byte[]));
    }

    [Fact]
    public void Double_click_requires_time_and_distance()
    {
        var d = new DoubleClickDetector(TimeSpan.FromMilliseconds(500), 4);
        var t = DateTimeOffset.UnixEpoch;
        Assert.False(d.Register(t, 0, 0));
        Assert.False(d.Register(t.AddMilliseconds(900), 0, 0));   // too slow
        Assert.False(d.Register(t.AddMilliseconds(1000), 50, 50)); // too far
        Assert.True(d.Register(t.AddMilliseconds(1100), 51, 50));
        Assert.False(d.Register(t.AddMilliseconds(1200), 51, 50)); // a triple click is not two doubles
    }
}

public class UtilityTests
{
    [Fact]
    public void Clock_offset_uses_median_and_ignores_bad_round_trips()
    {
        var c = new ClockOffsetTracker();
        var t = DateTimeOffset.UnixEpoch;
        c.AddSample(t, t.AddMilliseconds(100), t.AddMilliseconds(50 + 5000)); // device is 5 s behind
        Assert.InRange(c.OffsetMs, 4990, 5010);
        c.AddSample(t, t.AddSeconds(30), t.AddYears(1));                       // 30 s round trip: discarded
        Assert.InRange(c.OffsetMs, 4990, 5010);
        Assert.Equal(t.AddMilliseconds(c.OffsetMs), c.Correct(t));
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var b = new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), new Random(1));
        var d = Enumerable.Range(0, 12).Select(b.Next).ToArray();
        Assert.True(d[3] > d[0]);
        Assert.All(d, x => Assert.True(x <= TimeSpan.FromMinutes(5)));
        Assert.True(d[^1] >= TimeSpan.FromMinutes(4));
    }

    [Fact]
    public void Deterministic_guid_is_stable()
        => Assert.Equal(DeterministicGuid.Create("a|b"), DeterministicGuid.Create("a|b"));

    [Fact]
    public void Every_event_type_constant_is_registered_and_unique()
    {
        var consts = typeof(EventTypes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(consts.Count, consts.Distinct().Count());
        Assert.All(consts, c => Assert.True(EventTypes.IsKnown(c)));
    }
}
