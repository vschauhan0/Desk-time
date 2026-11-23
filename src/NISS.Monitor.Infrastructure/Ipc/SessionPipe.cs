using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Infrastructure.Collectors;
using NISS.Monitor.Infrastructure.Native;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Ipc;

/// <summary>Line-delimited JSON framing with a hard size cap, shared by both pipe ends.</summary>
internal static class PipeFraming
{
    public const int MaxLineChars = 128 * 1024;

    public static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= MaxLineChars) throw new InvalidDataException("Pipe message too large.");
            sb.Append(buf[0]);
        }
    }

    public static async Task WriteAsync(StreamWriter writer, PipeMessage message, CancellationToken ct)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, ProtocolJson.Options).AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Service side of the session channel. Session processes connect to \\.\pipe\NISSMonitor.Session, identify
/// themselves, then stream event drafts and live-state snapshots. The pipe is verified server-side: the client's
/// Windows session id must match its hello, and (by default) the client image must be the installed session exe.
/// </summary>
public sealed class SessionPipeServer : ICollector
{
    private static readonly HashSet<string> AllowedEventTypes =
    [
        EventTypes.ApplicationStarted, EventTypes.ApplicationStopped, EventTypes.ApplicationForeground, EventTypes.ApplicationBackground,
        EventTypes.SessionLock, EventTypes.SessionUnlock, EventTypes.ActiveStarted, EventTypes.IdleStarted, EventTypes.IdleEnded,
        EventTypes.MouseActivity, EventTypes.KeyboardActivity, EventTypes.AgentError,
    ];

    private readonly IEventSink _sink;
    private readonly ILiveStateStore _live;
    private readonly IPolicyProvider _policy;
    private readonly ICollectorRegistry _registry;
    private readonly LocalTelemetryStore _local;
    private readonly TimeProvider _time;
    private readonly AgentSettings _settings;
    private readonly ILogger<SessionPipeServer> _log;

    public SessionPipeServer(IEventSink sink, ILiveStateStore live, IPolicyProvider policy, ICollectorRegistry registry, LocalTelemetryStore local,
        TimeProvider time, IOptions<AgentSettings> settings, ILogger<SessionPipeServer> log)
    {
        _sink = sink; _live = live; _policy = policy; _registry = registry; _local = local; _time = time; _settings = settings.Value; _log = log;
    }

    public string Name => "SessionPipe";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _registry.Report(Name, "Running");
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try { server = Create(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _registry.Report(Name, "Degraded", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try { await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch { server.Dispose(); throw; }
            _ = Task.Run(() => HandleClientAsync(server, cancellationToken), cancellationToken);
        }
    }

    private NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_settings.Session.PipeName, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        int? sessionId = null;
        try
        {
            using (pipe)
            {
                if (!VerifyClient(pipe, out var clientSession, out var reason))
                {
                    _log.LogWarning("Rejected pipe client: {Reason}", reason);
                    return;
                }

                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = false };

                var first = await PipeFraming.ReadLineAsync(reader, ct).ConfigureAwait(false);
                var hello = first is null ? null : JsonSerializer.Deserialize<PipeMessage>(first, ProtocolJson.Options);
                if (hello is not { Kind: PipeMessage.HelloKind })
                {
                    _log.LogWarning("Pipe client did not say hello");
                    return;
                }
                sessionId = clientSession;
                var userName = hello.UserName;
                _log.LogInformation("Session agent connected: session {SessionId}, user {User}, version {Version}", sessionId, userName, hello.Version);

                void OnPolicy(AgentPolicy p) => _ = PushPolicyAsync(writer, p, ct);
                _policy.Changed += OnPolicy;
                try
                {
                    await PipeFraming.WriteAsync(writer, new PipeMessage { Kind = PipeMessage.ConfigKind, Policy = _policy.Current }, ct).ConfigureAwait(false);
                    await ReadLoopAsync(reader, sessionId.Value, userName, ct).ConfigureAwait(false);
                }
                finally { _policy.Changed -= OnPolicy; }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            if (ex is not OperationCanceledException) _log.LogInformation("Session pipe closed: {Message}", ex.Message);
        }
        finally
        {
            if (sessionId is { } sid)
            {
                _live.RemoveState(sid);
                _log.LogInformation("Session agent disconnected: session {SessionId}", sid);
            }
        }
    }

    private static readonly SemaphoreSlim PushGate = new(1, 1);

    private static async Task PushPolicyAsync(StreamWriter writer, AgentPolicy policy, CancellationToken ct)
    {
        await PushGate.WaitAsync(ct).ConfigureAwait(false);
        try { await PipeFraming.WriteAsync(writer, new PipeMessage { Kind = PipeMessage.ConfigKind, Policy = policy }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { PushGate.Release(); }
    }

    private async Task ReadLoopAsync(StreamReader reader, int sessionId, string? userName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await PipeFraming.ReadLineAsync(reader, ct).ConfigureAwait(false);
            if (line is null) return;
            var msg = JsonSerializer.Deserialize<PipeMessage>(line, ProtocolJson.Options);
            if (msg is null) continue;

            switch (msg.Kind)
            {
                case PipeMessage.StateKind when msg.State is not null:
                    // Identity fields come from the verified connection, not from the payload.
                    _live.Update(msg.State with { WindowsSessionId = sessionId, UserName = userName ?? msg.State.UserName, ReportedUtc = _time.GetUtcNow() });
                    _registry.Touch(Name);
                    break;

                case PipeMessage.EventKind when msg.Event is not null:
                    if (!AllowedEventTypes.Contains(msg.Event.EventType) || msg.Event.Payload.GetRawText().Length > 16_384) break;
                    if (msg.Event.EventType == EventTypes.ApplicationBackground
                        && ProtocolJson.FromElement<ApplicationBackgroundPayload>(msg.Event.Payload) is { } closed)
                        _local.ApplicationSessionClosed(closed);
                    await _sink.EmitAsync(new EventDraft(msg.Event.EventType, EventSources.Session, msg.Event.TimestampUtc, msg.Event.Payload, msg.Event.SessionId), ct).ConfigureAwait(false);
                    _registry.Touch(Name);
                    break;

                case PipeMessage.ByeKind:
                    return;
            }
        }
    }

    private bool VerifyClient(NamedPipeServerStream pipe, out int clientSession, out string reason)
    {
        clientSession = -1;
        reason = "";
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        if (!NativeMethods.GetNamedPipeClientSessionId(handle, out var sid) || !NativeMethods.GetNamedPipeClientProcessId(handle, out var pid))
        {
            reason = "cannot identify client";
            return false;
        }
        clientSession = (int)sid;
        if (!_settings.Session.RequireTrustedClientPath) return true; // development/test only
        var path = NativeMethods.GetProcessImagePath((int)pid);
        if (path is null) { reason = "cannot read client image"; return false; }
        if (!string.Equals(Path.GetFileName(path), AgentConstants.SessionExeName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unexpected client image {Path.GetFileName(path)}";
            return false;
        }
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, AgentConstants.SessionExeName));
        if (!string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
        {
            reason = "client is not the installed session agent";
            return false;
        }
        return true;
    }
}

/// <summary>Session-process side of the channel: reconnecting client + <see cref="IEventSink"/> for the session collectors.</summary>
public sealed class SessionPipeClient : IEventSink
{
    private readonly Channel<PipeMessage> _outbound = Channel.CreateBounded<PipeMessage>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly string _user;
    private readonly int _pid = Environment.ProcessId;
    private readonly string _version;
    private readonly string _pipeName;
    private volatile bool _connected;

    public SessionPipeClient(string userName, string version, string? pipeName = null)
    {
        _user = userName;
        _version = version;
        _pipeName = pipeName ?? AgentConstants.PipeName;
    }

    /// <summary>Raised when the service pushes a policy.</summary>
    public event Action<AgentPolicy>? PolicyReceived;

    /// <summary>True while the pipe to the service is open.</summary>
    public bool Connected => _connected;

    /// <inheritdoc />
    public ValueTask EmitAsync(EventDraft draft, CancellationToken cancellationToken = default)
    {
        _outbound.Writer.TryWrite(new PipeMessage
        {
            Kind = PipeMessage.EventKind,
            Event = new PipeEvent { EventType = draft.EventType, TimestampUtc = draft.TimestampUtc, Payload = draft.Payload, SessionId = draft.SessionId },
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>Wait (bounded) until buffered messages have been handed to the pipe - used on shutdown.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_connected && _outbound.Reader.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);
    }

    /// <summary>Send a live-state snapshot (states are not buffered while disconnected).</summary>
    public void SendState(SessionLiveState state)
    {
        if (_connected) _outbound.Writer.TryWrite(new PipeMessage { Kind = PipeMessage.StateKind, State = state });
    }

    /// <summary>Connect/reconnect forever until cancelled. Events are buffered (bounded) while the service is away.</summary>
    public async Task RunAsync(int windowsSessionId, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);

                await PipeFraming.WriteAsync(writer, new PipeMessage { Kind = PipeMessage.HelloKind, WindowsSessionId = windowsSessionId, UserName = _user, ProcessId = _pid, Version = _version }, ct).ConfigureAwait(false);
                _connected = true;
                delay = TimeSpan.FromSeconds(1);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = ReadLoopAsync(reader, linked.Token);
                var writeTask = WriteLoopAsync(writer, linked.Token);
                await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(readTask, writeTask).ConfigureAwait(false); } catch { /* one side failed: reconnect */ }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
            {
                if (ct.IsCancellationRequested) return;
            }
            finally { _connected = false; }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        await foreach (var m in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try { await PipeFraming.WriteAsync(writer, m, ct).ConfigureAwait(false); }
            catch
            {
                // Put the message back at the front of the line is not possible with a channel; re-queue it (order among
                // buffered events is preserved by the events' own timestamps and sequence assignment in the service).
                _outbound.Writer.TryWrite(m);
                throw;
            }
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await PipeFraming.ReadLineAsync(reader, ct).ConfigureAwait(false);
            if (line is null) return;
            var msg = JsonSerializer.Deserialize<PipeMessage>(line, ProtocolJson.Options);
            if (msg is { Kind: PipeMessage.ConfigKind, Policy: not null }) PolicyReceived?.Invoke(msg.Policy);
        }
    }
}
