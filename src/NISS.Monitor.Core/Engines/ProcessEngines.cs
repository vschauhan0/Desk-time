namespace NISS.Monitor.Core.Engines;

/// <summary>Result of comparing two process snapshots.</summary>
public sealed record ProcessDiff(IReadOnlyList<ProcessSnapshot> Started, IReadOnlyList<ProcessSnapshot> Stopped, bool IsBaseline);

/// <summary>
/// Compares consecutive process snapshots. A process is identified by (pid, name, parent pid) so a recycled PID
/// is reported as a stop followed by a start. The first snapshot is flagged as the baseline.
/// </summary>
public sealed class ProcessDiffEngine
{
    private readonly Dictionary<int, ProcessSnapshot> _known = [];
    private bool _first = true;

    /// <summary>Number of currently tracked processes.</summary>
    public int TrackedCount => _known.Count;

    /// <summary>Feed a new (already filtered) snapshot.</summary>
    public ProcessDiff Update(IReadOnlyList<ProcessSnapshot> snapshot)
    {
        var started = new List<ProcessSnapshot>();
        var stopped = new List<ProcessSnapshot>();
        var seen = new HashSet<int>(snapshot.Count);

        foreach (var p in snapshot)
        {
            seen.Add(p.Pid);
            if (_known.TryGetValue(p.Pid, out var old))
            {
                if (old.ParentPid == p.ParentPid && string.Equals(old.Name, p.Name, StringComparison.OrdinalIgnoreCase)) continue;
                stopped.Add(old);
            }
            _known[p.Pid] = p;
            started.Add(p);
        }

        foreach (var pid in _known.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            stopped.Add(_known[pid]);
            _known.Remove(pid);
        }

        var baseline = _first;
        _first = false;
        return new ProcessDiff(started, stopped, baseline);
    }
}

/// <summary>Decides which processes are worth reporting ("relevant process events").</summary>
public sealed class ProcessFilter
{
    /// <summary>System plumbing that would otherwise flood the timeline.</summary>
    public static readonly IReadOnlyList<string> DefaultExclusions =
    [
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "services", "lsass", "svchost",
        "fontdrvhost", "dwm", "winlogon", "sihost", "conhost", "ctfmon", "dllhost", "taskhostw", "runtimebroker",
        "backgroundtaskhost", "searchindexer", "searchprotocolhost", "searchfilterhost", "audiodg", "wudfhost",
        "spoolsv", "wmiprvse", "securityhealthservice", "securityhealthsystray", "textinputhost", "startmenuexperiencehost",
        "shellexperiencehost", "lockapp", "logonui", "applicationframehost", "msmpeng", "nissservice", "nissmonitor.service",
        "nissmonitor.session", "nissagent", "smartscreen", "unsecapp", "compattelrunner", "gamebarpresencewriter",
        "widgets", "widgetservice", "phoneexperiencehost", "systemsettingsbroker", "usernotificationcenter",
    ];

    private readonly HashSet<string> _excluded;
    private readonly bool _all;

    public ProcessFilter(string mode, IEnumerable<string>? extraExclusions)
    {
        _all = string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase);
        _excluded = new HashSet<string>(DefaultExclusions, StringComparer.OrdinalIgnoreCase);
        if (extraExclusions is not null)
            foreach (var e in extraExclusions) _excluded.Add(Normalize(e));
    }

    /// <summary>True when the process should produce events.</summary>
    public bool ShouldTrack(ProcessSnapshot p)
    {
        if (p.Pid <= 4) return false;
        if (_all) return true;
        if (p.SessionId <= 0) return false;
        return !_excluded.Contains(Normalize(p.Name));
    }

    private static string Normalize(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

/// <summary>Tracks window-owning applications: opened / closed, from periodic window enumerations.</summary>
public sealed class ApplicationTracker
{
    private readonly Dictionary<int, OpenWindowApp> _open = [];

    /// <summary>Currently open applications.</summary>
    public IReadOnlyCollection<OpenWindowApp> Open => _open.Values;

    /// <summary>Apply a fresh enumeration. Returns (opened, closed).</summary>
    public (IReadOnlyList<OpenWindowApp> Opened, IReadOnlyList<OpenWindowApp> Closed) Update(IReadOnlyList<OpenWindowApp> now)
    {
        var opened = new List<OpenWindowApp>();
        var closed = new List<OpenWindowApp>();
        var seen = new HashSet<int>();

        foreach (var app in now)
        {
            if (!seen.Add(app.ProcessId)) continue;
            if (_open.TryGetValue(app.ProcessId, out var existing))
            {
                _open[app.ProcessId] = app with { WindowTitle = app.WindowTitle ?? existing.WindowTitle };
                continue;
            }
            _open[app.ProcessId] = app;
            opened.Add(app);
        }

        foreach (var pid in _open.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            closed.Add(_open[pid]);
            _open.Remove(pid);
        }
        return (opened, closed);
    }
}
