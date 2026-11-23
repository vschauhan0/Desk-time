using System.Security.Cryptography;
using System.Text;

namespace NISS.Monitor.Core.Engines;

/// <summary>Exponential backoff with jitter.</summary>
public sealed class BackoffPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _cap;
    private readonly Random _random;

    public BackoffPolicy(TimeSpan baseDelay, TimeSpan cap, Random? random = null)
    {
        _baseDelay = baseDelay;
        _cap = cap;
        _random = random ?? Random.Shared;
    }

    /// <summary>Delay before attempt number <paramref name="attempt"/> (0-based). Includes +/-20% jitter.</summary>
    public TimeSpan Next(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(Math.Max(attempt, 0), 20));
        var ms = Math.Min(_cap.TotalMilliseconds, _baseDelay.TotalMilliseconds * factor);
        var jitter = 1 + (_random.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromMilliseconds(Math.Min(_cap.TotalMilliseconds, ms * jitter));
    }
}

/// <summary>
/// Tracks the difference between the server clock and this device's clock so timestamps can be corrected and
/// long offline periods do not skew reports. Uses the median of the last samples to shrug off outliers.
/// </summary>
public sealed class ClockOffsetTracker
{
    private readonly Queue<long> _samples = new();
    private readonly object _gate = new();
    private long _offsetMs;

    /// <summary>Latest smoothed offset (server - device) in milliseconds.</summary>
    public long OffsetMs { get { lock (_gate) return _offsetMs; } }

    /// <summary>Restore a persisted offset.</summary>
    public void Restore(long offsetMs) { lock (_gate) { _offsetMs = offsetMs; } }

    /// <summary>Add a sample from a request/response pair (half the round trip is assumed for each direction).</summary>
    public void AddSample(DateTimeOffset requestSentUtc, DateTimeOffset responseReceivedUtc, DateTimeOffset serverUtc)
    {
        var rtt = responseReceivedUtc - requestSentUtc;
        if (rtt < TimeSpan.Zero || rtt > TimeSpan.FromSeconds(10)) return; // unreliable measurement
        var midpoint = requestSentUtc + rtt / 2;
        var sample = (long)(serverUtc - midpoint).TotalMilliseconds;
        lock (_gate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > 5) _samples.Dequeue();
            var sorted = _samples.Order().ToArray();
            _offsetMs = sorted[sorted.Length / 2];
        }
    }

    /// <summary>Apply the offset to a device timestamp.</summary>
    public DateTimeOffset Correct(DateTimeOffset deviceUtc) => deviceUtc.AddMilliseconds(OffsetMs);
}

/// <summary>Deterministic GUIDs so replays of the same fact produce the same id (idempotent ingestion).</summary>
public static class DeterministicGuid
{
    /// <summary>Guid derived from SHA-256 of <paramref name="key"/>.</summary>
    public static Guid Create(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Detects login/logout/connect/disconnect from successive WTS session tables.</summary>
public sealed class SessionDiffEngine
{
    /// <summary>One interactive session as seen by WTS.</summary>
    public sealed record WtsSession(int SessionId, string State, string? UserName, string? Domain, string? ClientName, DateTimeOffset? LogonTimeUtc);

    /// <summary>A detected change.</summary>
    public sealed record Change(string Kind, WtsSession Session);

    public const string Login = "LOGIN";
    public const string Logout = "LOGOUT";
    public const string Connect = "CONNECT";
    public const string Disconnect = "DISCONNECT";

    private readonly Dictionary<int, WtsSession> _known = [];

    /// <summary>Currently known sessions.</summary>
    public IReadOnlyCollection<WtsSession> Known => _known.Values;

    /// <summary>Seed from persisted state so an agent restart does not replay logins.</summary>
    public void Seed(IEnumerable<WtsSession> sessions)
    {
        foreach (var s in sessions) _known[s.SessionId] = s;
    }

    /// <summary>Apply the current table (sessions with no user are ignored).</summary>
    public IReadOnlyList<Change> Update(IEnumerable<WtsSession> current)
    {
        var changes = new List<Change>();
        var now = current.Where(s => s.SessionId != 0 && !string.IsNullOrEmpty(s.UserName)).ToDictionary(s => s.SessionId);

        foreach (var (id, session) in now)
        {
            if (!_known.TryGetValue(id, out var old) || !SameLogon(old, session))
            {
                if (old is not null) changes.Add(new Change(Logout, old));
                changes.Add(new Change(Login, session));
                if (IsDisconnected(session.State)) changes.Add(new Change(Disconnect, session));
            }
            else if (IsDisconnected(old.State) != IsDisconnected(session.State))
            {
                changes.Add(new Change(IsDisconnected(session.State) ? Disconnect : Connect, session));
            }
            _known[id] = session;
        }

        foreach (var id in _known.Keys.Where(k => !now.ContainsKey(k)).ToArray())
        {
            changes.Add(new Change(Logout, _known[id]));
            _known.Remove(id);
        }
        return changes;
    }

    private static bool IsDisconnected(string state) => state is "Disconnected" or "Down" or "Reset" or "Shadow";

    private static bool SameLogon(WtsSession a, WtsSession b)
        => string.Equals(a.UserName, b.UserName, StringComparison.OrdinalIgnoreCase)
           && (a.LogonTimeUtc is null || b.LogonTimeUtc is null || a.LogonTimeUtc == b.LogonTimeUtc);
}
