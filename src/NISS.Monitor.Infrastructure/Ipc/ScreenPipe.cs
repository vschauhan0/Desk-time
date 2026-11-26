using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Infrastructure.Native;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Ipc;

/// <summary>Length-prefixed binary framing for screen frames: [4B headerLen][header JSON][4B payloadLen][JPEG bytes].</summary>
internal static class ScreenFraming
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public static byte[] Int32BE(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    public static int ToInt32BE(byte[] b) => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];

    public static async Task<byte[]?> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        if (count == 0) return [];
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (n == 0) return null; // peer closed
            off += n;
        }
        return buf;
    }
}

/// <summary>
/// Service side of the screen channel: the session helper connects and streams binary JPEG frames
/// (periodic + live). Each frame is handed to <c>onFrame</c>. When configured, the client image must be
/// the installed session exe (same check as the main session pipe).
/// </summary>
public sealed class ScreenPipeServer(
    string pipeName,
    bool requireTrustedClientPath,
    Func<ScreenFrameHeader, byte[], CancellationToken, Task> onFrame,
    ILogger? log = null) : IAsyncDisposable
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
            catch (Exception ex) { log?.LogWarning("Screen pipe create failed: {Message}", ex.Message); await Task.Delay(1000, token).ConfigureAwait(false); continue; }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                if (!Verify(pipe, out var reason))
                {
                    log?.LogWarning("Rejected screen pipe client: {Reason}", reason);
                    continue;
                }
                await ReadFramesAsync(pipe, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log?.LogInformation("Screen pipe closed: {Message}", ex.Message); }
            finally { await pipe.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, ScreenFraming.MaxPayloadBytes + 4096, security);
    }

    private bool Verify(NamedPipeServerStream pipe, out string reason)
    {
        reason = "";
        if (!requireTrustedClientPath) return true;
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        if (!NativeMethods.GetNamedPipeClientProcessId(handle, out var pid)) { reason = "cannot identify client"; return false; }
        var path = NativeMethods.GetProcessImagePath((int)pid);
        if (path is null) { reason = "cannot read client image"; return false; }
        if (!string.Equals(Path.GetFileName(path), AgentConstants.SessionExeName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unexpected client image {Path.GetFileName(path)}";
            return false;
        }
        // Full installed path, not just the filename, so a renamed copy elsewhere cannot impersonate the session agent.
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, AgentConstants.SessionExeName));
        if (!string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
        {
            reason = "client is not the installed session agent";
            return false;
        }
        return true;
    }

    private async Task ReadFramesAsync(Stream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var headerLen = await ScreenFraming.ReadExactAsync(pipe, 4, ct).ConfigureAwait(false);
            if (headerLen is null) return;
            var hlen = ScreenFraming.ToInt32BE(headerLen);
            if (hlen <= 0 || hlen > ScreenFraming.MaxHeaderBytes) throw new InvalidDataException("Bad screen frame header length.");
            var headerBytes = await ScreenFraming.ReadExactAsync(pipe, hlen, ct).ConfigureAwait(false);
            if (headerBytes is null) return;
            var header = JsonSerializer.Deserialize<ScreenFrameHeader>(headerBytes, ProtocolJson.Options)
                ?? throw new InvalidDataException("Bad screen frame header.");

            var payloadLen = await ScreenFraming.ReadExactAsync(pipe, 4, ct).ConfigureAwait(false);
            if (payloadLen is null) return;
            var plen = ScreenFraming.ToInt32BE(payloadLen);
            if (plen < 0 || plen > ScreenFraming.MaxPayloadBytes) throw new InvalidDataException("Bad screen frame payload length.");
            var payload = await ScreenFraming.ReadExactAsync(pipe, plen, ct).ConfigureAwait(false);
            if (payload is null) return;

            await onFrame(header, payload, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _own.CancelAsync().ConfigureAwait(false);
        _own.Dispose();
    }
}

/// <summary>Session-process side: connects to the screen pipe and sends binary frames.</summary>
public sealed class ScreenPipeClient(string pipeName) : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
    }

    public async Task SendFrameAsync(ScreenFrameHeader header, byte[] payload, CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected) throw new InvalidOperationException("Screen pipe is not connected.");
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, ProtocolJson.Options);
        await _pipe.WriteAsync(ScreenFraming.Int32BE(headerBytes.Length), ct).ConfigureAwait(false);
        await _pipe.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await _pipe.WriteAsync(ScreenFraming.Int32BE(payload.Length), ct).ConfigureAwait(false);
        if (payload.Length > 0) await _pipe.WriteAsync(payload, ct).ConfigureAwait(false);
        await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
