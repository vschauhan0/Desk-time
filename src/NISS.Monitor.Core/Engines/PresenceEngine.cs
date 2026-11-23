using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Engines;

/// <summary>Local presence of the human at the machine.</summary>
public enum PresenceState { Unknown, Active, Idle, Locked }

/// <summary>A presence transition. <see cref="EventType"/> is ACTIVE_STARTED, IDLE_STARTED or IDLE_ENDED.</summary>
/// <param name="IdleSinceUtc">Time of the last real input (not the time the threshold elapsed).</param>
public sealed record PresenceChange(string EventType, DateTimeOffset AtUtc, DateTimeOffset? IdleSinceUtc, double? IdleSeconds);

/// <summary>
/// Idle/active state machine. Driven by "time since last input" (GetLastInputInfo), so screen-on time is never
/// mistaken for activity. Lock/unlock are fed in separately.
/// </summary>
public sealed class PresenceEngine
{
    private TimeSpan _threshold;
    private DateTimeOffset _idleSince;

    public PresenceEngine(TimeSpan threshold) => _threshold = threshold;

    /// <summary>Current state.</summary>
    public PresenceState State { get; private set; } = PresenceState.Unknown;

    /// <summary>Change the idle threshold at runtime (policy update).</summary>
    public void SetThreshold(TimeSpan threshold) => _threshold = threshold;

    /// <summary>Evaluate the machine given how long ago the last input was.</summary>
    public IReadOnlyList<PresenceChange> Tick(DateTimeOffset nowUtc, TimeSpan idleFor)
    {
        var list = new List<PresenceChange>(2);
        if (State == PresenceState.Locked) return list;

        var isIdle = idleFor >= _threshold;
        switch (State)
        {
            case PresenceState.Unknown:
                if (isIdle) StartIdle(list, nowUtc, idleFor);
                else { State = PresenceState.Active; list.Add(new PresenceChange(EventTypes.ActiveStarted, nowUtc, null, null)); }
                break;
            case PresenceState.Active when isIdle:
                StartIdle(list, nowUtc, idleFor);
                break;
            case PresenceState.Idle when !isIdle:
                list.Add(new PresenceChange(EventTypes.IdleEnded, nowUtc, _idleSince, Math.Max(0, (nowUtc - _idleSince).TotalSeconds)));
                State = PresenceState.Active;
                list.Add(new PresenceChange(EventTypes.ActiveStarted, nowUtc, null, null));
                break;
        }
        return list;
    }

    /// <summary>Lock/unlock. Locking ends an idle period (lock time is tracked separately as LOCKED).</summary>
    public IReadOnlyList<PresenceChange> SetLocked(bool locked, DateTimeOffset nowUtc)
    {
        var list = new List<PresenceChange>(1);
        if (locked)
        {
            if (State == PresenceState.Idle)
                list.Add(new PresenceChange(EventTypes.IdleEnded, nowUtc, _idleSince, Math.Max(0, (nowUtc - _idleSince).TotalSeconds)));
            State = PresenceState.Locked;
        }
        else if (State == PresenceState.Locked)
        {
            State = PresenceState.Unknown; // next Tick decides ACTIVE_STARTED vs IDLE_STARTED
        }
        return list;
    }

    private void StartIdle(List<PresenceChange> list, DateTimeOffset nowUtc, TimeSpan idleFor)
    {
        _idleSince = nowUtc - idleFor;
        State = PresenceState.Idle;
        list.Add(new PresenceChange(EventTypes.IdleStarted, nowUtc, _idleSince, null));
    }
}
