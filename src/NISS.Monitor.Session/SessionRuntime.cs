using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Interactive;
using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Session;

/// <summary>
/// Orchestrates everything that needs the interactive desktop: foreground app, idle/active, lock/unlock, input
/// activity counters and the list of open applications. Pure decision logic lives in the Core engines; this class only
/// feeds them samples and turns their output into events on the pipe to the service.
/// </summary>
public sealed class SessionRuntime
{
    private static readonly TimeSpan OpenAppScanInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(5);

    private readonly IForegroundMonitor _foreground;
    private readonly IIdleMonitor _idle;
    private readonly MessageWindowMonitor _window;
    private readonly IEventSink _sink;
    private readonly SessionPipeClient _pipe;
    private readonly TimeProvider _time;
    private readonly SessionLog _log;
    private readonly int _windowsSessionId;
    private readonly string _user;
    private readonly Guid _sessionGuid = Guid.NewGuid();

    private readonly PresenceEngine _presence;
    private readonly ApplicationSessionEngine _apps = new();
    private readonly ApplicationTracker _tracker = new();
    private readonly Dictionary<int, DateTimeOffset> _firstSeen = [];
    private readonly InputBucketAggregator _input;
    private volatile AgentPolicy _policy = new();
    private bool _lockedSent;
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private DateTimeOffset _lastState = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCursorSample = DateTimeOffset.MinValue;
    private string _lastPresence = Presence.Unknown;

    public SessionRuntime(IForegroundMonitor foreground, IIdleMonitor idle, MessageWindowMonitor window, SessionPipeClient pipe,
        TimeProvider time, SessionLog log, int windowsSessionId, string user)
    {
        _foreground = foreground;
        _idle = idle;
        _window = window;
        _pipe = pipe;
        _sink = pipe;
        _time = time;
        _log = log;
        _windowsSessionId = windowsSessionId;
        _user = user;
        _presence = new PresenceEngine(TimeSpan.FromMinutes(_policy.IdleThresholdMinutes));
        _input = new InputBucketAggregator(time, _policy.InputBucketSeconds, CursorReader.DoubleClickTime);
        _pipe.PolicyReceived += OnPolicy;
    }

    /// <summary>
    /// Optional observer of the foreground window (process name, window title), invoked on every foreground poll.
    /// Screen monitoring subscribes to this to detect flagged activity, so it sees exactly the same sample the event
    /// pipeline does - including the <c>CaptureWindowTitles</c> privacy gate, which nulls the title when it is off.
    /// </summary>
    public Action<string?, string?>? ForegroundObserver { get; set; }

    private void OnPolicy(AgentPolicy p)
    {
        _policy = p;
        _presence.SetThreshold(TimeSpan.FromMinutes(Math.Max(1, p.IdleThresholdMinutes)));
        _input.SetBucketSeconds(p.InputBucketSeconds);
        _log.Info($"Policy v{p.Version} applied (idle {p.IdleThresholdMinutes} min, poll {p.ForegroundPollMilliseconds} ms)");
    }

    /// <summary>Run until cancelled. All failures inside one tick are contained.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var inputTask = _window.RunAsync(_input, ct);
        var pipeTask = _pipe.RunAsync(_windowsSessionId, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Error("Tick failed: " + ex.Message);
                    await EmitErrorAsync(ex.Message, ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                await Task.Delay(Math.Clamp(_policy.ForegroundPollMilliseconds, 250, 10_000), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        await Task.WhenAny(Task.WhenAll(inputTask, pipeTask), Task.Delay(1000)).ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var policy = _policy;
        var idleFor = _idle.GetIdleTime();

        // lock state: WTS notification OR the input desktop is not the default desktop
        var locked = _window.IsLocked || !DesktopState.IsDefaultDesktopActive();
        if (locked != _lockedSent)
        {
            _lockedSent = locked;
            await EmitAsync(locked ? EventTypes.SessionLock : EventTypes.SessionUnlock, now, StatePayload(locked ? "Locked" : "Unlocked"), ct).ConfigureAwait(false);
            await EmitPresenceAsync(_presence.SetLocked(locked, now), now, ct).ConfigureAwait(false);
        }

        await EmitPresenceAsync(_presence.Tick(now, idleFor), now, ct).ConfigureAwait(false);

        var sample = locked ? null : _foreground.Sample(policy.CaptureWindowTitles);
        try { ForegroundObserver?.Invoke(sample?.ProcessName, sample?.WindowTitle); }
        catch (Exception ex) { _log.Error("Foreground observer failed: " + ex.Message); }
        var interacting = idleFor <= TimeSpan.FromSeconds(Math.Max(5, policy.InteractionWindowSeconds));
        var idle = _presence.State == PresenceState.Idle;
        foreach (var change in _apps.Tick(new AppTick(now, sample, locked, idle, interacting)))
            await EmitAppChangeAsync(change, ct).ConfigureAwait(false);

        if (now - _lastScan >= OpenAppScanInterval)
        {
            _lastScan = now;
            await ScanOpenApplicationsAsync(now, policy, ct).ConfigureAwait(false);
        }

        if (policy.CursorSampling.Enabled && now - _lastCursorSample >= TimeSpan.FromSeconds(Math.Max(1, policy.CursorSampling.IntervalSeconds)))
        {
            _lastCursorSample = now;
            if (CursorReader.TryGet() is { } pos) _input.RecordCursorSample(pos.X, pos.Y);
        }

        await FlushInputAsync(false, ct).ConfigureAwait(false);
        SendState(now, idleFor, interacting, sample);
    }

    private async Task EmitPresenceAsync(IReadOnlyList<PresenceChange> changes, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var c in changes)
        {
            var payload = new IdlePayload(_windowsSessionId, _user, c.IdleSinceUtc, c.IdleSeconds, (int)TimeSpan.FromMinutes(_policy.IdleThresholdMinutes).TotalSeconds);
            await EmitAsync(c.EventType, c.AtUtc, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task EmitAppChangeAsync(AppSessionChange change, CancellationToken ct)
    {
        var s = change.Session;
        if (change.Kind == AppSessionChangeKind.Started)
        {
            await EmitAsync(EventTypes.ApplicationForeground, s.StartedUtc,
                new ApplicationForegroundPayload(s.Id, s.ProcessId, s.ProcessName, s.ExecutablePath, s.WindowTitle, change.PreviousProcessName, _windowsSessionId), ct).ConfigureAwait(false);
        }
        else
        {
            await EmitAsync(EventTypes.ApplicationBackground, s.EndedUtc ?? s.StartedUtc,
                new ApplicationBackgroundPayload(s.Id, s.ProcessId, s.ProcessName, s.ExecutablePath, s.WindowTitle, s.StartedUtc, s.EndedUtc ?? s.StartedUtc,
                    Math.Round(s.DurationSeconds, 1), Math.Round(s.InteractionSeconds, 1), Math.Round(s.IdleSeconds, 1), change.EndReason ?? AppSessionEndReasons.Switched, _windowsSessionId), ct).ConfigureAwait(false);
        }
    }

    private async Task ScanOpenApplicationsAsync(DateTimeOffset now, AgentPolicy policy, CancellationToken ct)
    {
        var (opened, closed) = _tracker.Update(_foreground.EnumerateOpenApplications(policy.CaptureWindowTitles));
        foreach (var a in opened)
        {
            _firstSeen[a.ProcessId] = now;
            await EmitAsync(EventTypes.ApplicationStarted, now,
                new ApplicationLifecyclePayload(a.ProcessId, a.ProcessName, a.ExecutablePath, a.WindowTitle, _windowsSessionId, now, null), ct).ConfigureAwait(false);
        }
        foreach (var a in closed)
        {
            _firstSeen.Remove(a.ProcessId);
            await EmitAsync(EventTypes.ApplicationStopped, now,
                new ApplicationLifecyclePayload(a.ProcessId, a.ProcessName, a.ExecutablePath, a.WindowTitle, _windowsSessionId, null, now), ct).ConfigureAwait(false);
            foreach (var change in _apps.NotifyProcessExited(a.ProcessId, now)) await EmitAppChangeAsync(change, ct).ConfigureAwait(false);
        }
    }

    private async Task FlushInputAsync(bool force, CancellationToken ct)
    {
        var bucket = _input.TryFlush(force);
        if (bucket is null) return;
        if (bucket.HasMouse)
            await EmitAsync(EventTypes.MouseActivity, bucket.LastMouseUtc ?? bucket.StartUtc,
                new InputActivityPayload(bucket.StartUtc, bucket.Seconds, null, null, bucket.MouseMoveEvents, bucket.Clicks, bucket.DoubleClicks, bucket.WheelEvents, bucket.LastMouseUtc, bucket.CursorX, bucket.CursorY), ct).ConfigureAwait(false);
        if (bucket.HasKeyboard)
            await EmitAsync(EventTypes.KeyboardActivity, bucket.LastKeyboardUtc ?? bucket.StartUtc,
                new InputActivityPayload(bucket.StartUtc, bucket.Seconds, bucket.KeyboardEvents, bucket.LastKeyboardUtc, null, null, null, null, null, null, null), ct).ConfigureAwait(false);
    }

    private void SendState(DateTimeOffset now, TimeSpan idleFor, bool interacting, ForegroundSample? sample)
    {
        var presence = _presence.State switch
        {
            PresenceState.Locked => Presence.Locked,
            PresenceState.Idle => Presence.Idle,
            PresenceState.Active => Presence.Active,
            _ => Presence.Unknown,
        };
        var due = now - _lastState >= StateInterval || presence != _lastPresence;
        if (!due) return;
        _lastState = now;
        _lastPresence = presence;

        var foreground = sample is null ? null : new ForegroundInfo(sample.ProcessId, sample.ProcessName, sample.ExecutablePath, sample.WindowTitle);
        var open = _tracker.Open.Select(a => new OpenApplication(a.ProcessId, a.ProcessName, a.ExecutablePath, a.WindowTitle,
            _firstSeen.TryGetValue(a.ProcessId, out var t) ? t : null)).ToList();
        DateTimeOffset? lastInput = _input.LastInputUtc ?? now - idleFor;
        _pipe.SendState(new SessionLiveState(_windowsSessionId, _user, presence, foreground, open, Math.Round(idleFor.TotalSeconds, 0), interacting, lastInput, now));
    }

    private async Task ShutdownAsync()
    {
        try
        {
            var now = _time.GetUtcNow();
            foreach (var change in _apps.Flush(now, AppSessionEndReasons.Logout)) await EmitAppChangeAsync(change, CancellationToken.None).ConfigureAwait(false);
            await FlushInputAsync(true, CancellationToken.None).ConfigureAwait(false);
            await _pipe.DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Error("Shutdown flush failed: " + ex.Message); }
    }

    private ValueTask EmitAsync<T>(string type, DateTimeOffset at, T payload, CancellationToken ct)
        => _sink.EmitAsync(EventDraft.Create(type, EventSources.Session, at, payload, _sessionGuid), ct);

    private SessionStatePayload StatePayload(string state) => new(_windowsSessionId, _user, null, state, null);

    private ValueTask EmitErrorAsync(string message, CancellationToken ct)
        => EmitAsync(EventTypes.AgentError, _time.GetUtcNow(),
            new AgentPayload("session", null, "COLLECTOR_ERROR", message.Length > 300 ? message[..300] : message, "NISSMonitor.Session", null, null), ct);
}
