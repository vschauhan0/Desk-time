namespace NISS.Monitor.Core.Engines;

/// <summary>Recognises double-clicks from two clicks close in time and space.</summary>
public sealed class DoubleClickDetector
{
    private readonly TimeSpan _window;
    private readonly int _distance;
    private DateTimeOffset _lastClick = DateTimeOffset.MinValue;
    private (int X, int Y) _lastPos;
    private bool _lastWasDouble;

    public DoubleClickDetector(TimeSpan window, int distancePixels)
    {
        _window = window;
        _distance = distancePixels;
    }

    /// <summary>Register a click; true when it completes a double-click.</summary>
    public bool Register(DateTimeOffset now, int x, int y)
    {
        var isDouble = !_lastWasDouble
                       && now - _lastClick <= _window
                       && Math.Abs(x - _lastPos.X) <= _distance
                       && Math.Abs(y - _lastPos.Y) <= _distance;
        _lastClick = now;
        _lastPos = (x, y);
        _lastWasDouble = isDouble;
        return isDouble;
    }
}

/// <summary>
/// Thread-safe input counters flushed into fixed-size buckets. Only counts and timestamps exist - the API has no
/// way to record which key was pressed.
/// </summary>
public sealed class InputBucketAggregator
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly DoubleClickDetector _doubleClicks;
    private int _bucketSeconds;
    private DateTimeOffset _bucketStart;
    private int _keys, _moves, _clicks, _doubles, _wheels;
    private DateTimeOffset? _lastKey, _lastMouse;
    private int? _cursorX, _cursorY;
    private DateTimeOffset? _lastInputUtc;

    public InputBucketAggregator(TimeProvider time, int bucketSeconds, TimeSpan doubleClickTime, int doubleClickDistance = 4)
    {
        _time = time;
        _bucketSeconds = Math.Max(5, bucketSeconds);
        _doubleClicks = new DoubleClickDetector(doubleClickTime, doubleClickDistance);
        _bucketStart = Floor(time.GetUtcNow());
    }

    /// <summary>Most recent input of any kind seen by the aggregator.</summary>
    public DateTimeOffset? LastInputUtc { get { lock (_gate) return _lastInputUtc; } }

    /// <summary>Change the bucket length (policy update).</summary>
    public void SetBucketSeconds(int seconds) { lock (_gate) _bucketSeconds = Math.Max(5, seconds); }

    /// <summary>A key went down. The key itself is never observed.</summary>
    public void RecordKeyDown()
    {
        var now = _time.GetUtcNow();
        lock (_gate) { _keys++; _lastKey = now; _lastInputUtc = now; }
    }

    /// <summary>The pointer moved.</summary>
    public void RecordMouseMove()
    {
        var now = _time.GetUtcNow();
        lock (_gate) { _moves++; _lastMouse = now; _lastInputUtc = now; }
    }

    /// <summary>A mouse button went down at (x, y).</summary>
    public void RecordClick(int x, int y)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _clicks++;
            if (_doubleClicks.Register(now, x, y)) _doubles++;
            _lastMouse = now; _lastInputUtc = now;
        }
    }

    /// <summary>The wheel turned.</summary>
    public void RecordWheel()
    {
        var now = _time.GetUtcNow();
        lock (_gate) { _wheels++; _lastMouse = now; _lastInputUtc = now; }
    }

    /// <summary>Record an optional cursor sample (only when the policy enables sampling).</summary>
    public void RecordCursorSample(int x, int y)
    {
        lock (_gate) { _cursorX = x; _cursorY = y; }
    }

    /// <summary>Flush when the bucket has elapsed (or <paramref name="force"/>). Returns null when nothing to report.</summary>
    public InputBucket? TryFlush(bool force = false)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!force && now < _bucketStart.AddSeconds(_bucketSeconds)) return null;

            var empty = _keys == 0 && _moves == 0 && _clicks == 0 && _doubles == 0 && _wheels == 0;
            InputBucket? bucket = empty ? null : new InputBucket(
                _bucketStart, _bucketSeconds, _keys, _lastKey, _moves, _clicks, _doubles, _wheels, _lastMouse, _cursorX, _cursorY);

            _keys = _moves = _clicks = _doubles = _wheels = 0;
            _lastKey = _lastMouse = null;
            _cursorX = _cursorY = null;
            _bucketStart = Floor(now);
            return bucket;
        }
    }

    private DateTimeOffset Floor(DateTimeOffset t)
    {
        var s = Math.Max(5, _bucketSeconds);
        var ticks = t.UtcTicks - t.UtcTicks % TimeSpan.FromSeconds(s).Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
