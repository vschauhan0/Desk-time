using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Ipc;

/// <summary>
/// Service -&gt; session control channel (line-delimited JSON <see cref="ScreenCommand"/>): the session helper hosts it,
/// the service connects and writes policy updates and live start/stop/switch commands. Separate from the event pipe
/// and the binary screen-frame pipe so none interferes with the others.
/// </summary>
public static class ScreenControlPipe
{
    public const string PipeName = "NISSMonitor.ScreenControl";

    public static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= 64 * 1024) throw new InvalidDataException("Screen control message too large.");
            sb.Append(buf[0]);
        }
    }
}

/// <summary>Session-hosted server: reads <see cref="ScreenCommand"/>s and hands them to <paramref name="onCommand"/>.</summary>
public sealed class ScreenControlPipeServer(Func<ScreenCommand, CancellationToken, Task> onCommand) : IAsyncDisposable
{
    private readonly CancellationTokenSource _own = new();

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _own.Token);
        var token = linked.Token;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = Create(); }
            catch { await Task.Delay(1000, token).ConfigureAwait(false); continue; }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                while (!token.IsCancellationRequested)
                {
                    var line = await ScreenControlPipe.ReadLineAsync(reader, token).ConfigureAwait(false);
                    if (line is null) break;
                    var cmd = JsonSerializer.Deserialize<ScreenCommand>(line, ProtocolJson.Options);
                    if (cmd is not null) await onCommand(cmd, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* reconnect */ }
            finally { await pipe.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private static NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ScreenControlPipe.PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 64 * 1024, security);
    }

    public async ValueTask DisposeAsync()
    {
        await _own.CancelAsync().ConfigureAwait(false);
        _own.Dispose();
    }
}

/// <summary>Service-side client: connects to the session's control pipe and writes commands.</summary>
public sealed class ScreenControlPipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _pipe = new NamedPipeClientStream(".", ScreenControlPipe.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = false };
    }

    public async Task SendAsync(ScreenCommand command, CancellationToken ct)
    {
        if (_writer is null) throw new InvalidOperationException("Screen control pipe is not connected.");
        await _writer.WriteLineAsync(JsonSerializer.Serialize(command, ProtocolJson.Options).AsMemory(), ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
