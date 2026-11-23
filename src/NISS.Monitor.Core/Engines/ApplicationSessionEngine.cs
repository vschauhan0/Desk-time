using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Engines;

/// <summary>An in-progress or finished foreground session of one application.</summary>
public sealed class ForegroundSession
{
    public ForegroundSession(Guid id, ForegroundSample app, DateTimeOffset startedUtc)
    {
        Id = id;
        ProcessId = app.ProcessId;
        ProcessName = app.ProcessName;
        ExecutablePath = app.ExecutablePath;
        WindowTitle = app.WindowTitle;
        StartedUtc = startedUtc;
    }

    public Guid Id { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }
    public string? ExecutablePath { get; }
    public string? WindowTitle { get; }
    public DateTimeOffset StartedUtc { get; }
    public DateTimeOffset? EndedUtc { get; internal set; }

    /// <summary>Seconds of this session with recent input ("actively used").</summary>
    public double InteractionSeconds { get; internal set; }

    /// <summary>Seconds of this session where the user was idle past the threshold.</summary>
    public double IdleSeconds { get; internal set; }

    /// <summary>Wall-clock length (foreground time).</summary>
    public double DurationSeconds => Math.Max(0, ((EndedUtc ?? StartedUtc) - StartedUtc).TotalSeconds);
}

/// <summary>What happened on a tick.</summary>
public enum AppSessionChangeKind { Started, Ended }

/// <summary>A foreground transition emitted by <see cref="ApplicationSessionEngine"/>.</summary>
public sealed record AppSessionChange(AppSessionChangeKind Kind, ForegroundSession Session, string? PreviousProcessName, string? EndReason);

/// <summary>One sample fed to the engine.</summary>
/// <param name="Foreground">Current foreground application or null.</param>
/// <param name="Locked">Session is locked (no application is "in front" while locked).</param>
/// <param name="Idle">User idle beyond the idle threshold.</param>
/// <param name="Interacting">Input seen within the interaction window.</param>
public readonly record struct AppTick(DateTimeOffset NowUtc, ForegroundSample? Foreground, bool Locked, bool Idle, bool Interacting);

/// <summary>
/// Turns 1-second foreground samples into application sessions (Excel 09:00:10 -> 09:35:20 = 35m10s) instead of
/// storing one row per sample. Also accumulates how much of each session was interactive vs idle, which is what
/// separates "open/foreground" from "actively used".
/// </summary>
public sealed class ApplicationSessionEngine
{
    private readonly TimeSpan _maxGap;
    private readonly TimeSpan _maxTickCredit;
    private ForegroundSession? _current;
    private DateTimeOffset? _lastTickUtc;
    private string? _lastClosedName;

    /// <param name="maxGap">A gap between ticks larger than this is treated as suspend/hibernate.</param>
    public ApplicationSessionEngine(TimeSpan? maxGap = null, TimeSpan? maxTickCredit = null)
    {
        _maxGap = maxGap ?? TimeSpan.FromSeconds(20);
        _maxTickCredit = maxTickCredit ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The session currently in the foreground (if any).</summary>
    public ForegroundSession? Current => _current;

    /// <summary>Process one sample. Returns zero, one or two changes (an end followed by a start).</summary>
    public IReadOnlyList<AppSessionChange> Tick(AppTick tick)
    {
        var changes = new List<AppSessionChange>(2);
        var now = tick.NowUtc;

        if (_lastTickUtc is { } last)
        {
            var gap = now - last;
            if (_current is not null && gap > _maxGap)
            {
                // The machine slept (or the process was frozen): end at the last time we knew the app was in front.
                Close(changes, last, AppSessionEndReasons.Suspended);
            }
            else if (_current is not null && gap > TimeSpan.Zero)
            {
                var credit = gap > _maxTickCredit ? _maxTickCredit : gap;
                if (tick.Interacting) _current.InteractionSeconds += credit.TotalSeconds;
                if (tick.Idle) _current.IdleSeconds += credit.TotalSeconds;
            }
        }
        _lastTickUtc = now;

        var effective = tick.Locked ? null : tick.Foreground;

        if (_current is not null && (effective is null || !SameApp(_current, effective)))
        {
            Close(changes, now, tick.Locked ? AppSessionEndReasons.Locked : AppSessionEndReasons.Switched);
        }

        if (_current is null && effective is not null)
        {
            _current = new ForegroundSession(Guid.NewGuid(), effective, now);
            changes.Add(new AppSessionChange(AppSessionChangeKind.Started, _current, _lastClosedName, null));
        }

        return changes;
    }

    /// <summary>Close the open session (shutdown, logout, agent stop).</summary>
    public IReadOnlyList<AppSessionChange> Flush(DateTimeOffset nowUtc, string reason)
    {
        var changes = new List<AppSessionChange>(1);
        if (_current is not null) Close(changes, nowUtc, reason);
        _lastTickUtc = null;
        return changes;
    }

    /// <summary>Tell the engine an application process exited so its session ends with the right reason.</summary>
    public IReadOnlyList<AppSessionChange> NotifyProcessExited(int processId, DateTimeOffset nowUtc)
    {
        var changes = new List<AppSessionChange>(1);
        if (_current is not null && _current.ProcessId == processId) Close(changes, nowUtc, AppSessionEndReasons.ProcessExited);
        return changes;
    }

    private void Close(List<AppSessionChange> changes, DateTimeOffset endUtc, string reason)
    {
        var s = _current!;
        s.EndedUtc = endUtc < s.StartedUtc ? s.StartedUtc : endUtc;
        _lastClosedName = s.ProcessName;
        _current = null;
        changes.Add(new AppSessionChange(AppSessionChangeKind.Ended, s, null, reason));
    }

    private static bool SameApp(ForegroundSession s, ForegroundSample f)
        => s.ProcessId == f.ProcessId && string.Equals(s.ProcessName, f.ProcessName, StringComparison.OrdinalIgnoreCase);
}
