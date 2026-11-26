using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Live;

/// <summary>Posts one periodic screenshot to the server. Abstracts the HTTP client so the uploader is unit-testable.</summary>
public interface IScreenshotUpload
{
    Task<bool> PostAsync(ScreenFrameHeader header, string frameId, byte[] jpeg, CancellationToken ct);
}

/// <summary>Real upload path: posts through the authenticated, certificate-pinned <see cref="ServerApiClient"/>.</summary>
public sealed class ServerScreenshotUpload(ServerApiClient client) : IScreenshotUpload
{
    public Task<bool> PostAsync(ScreenFrameHeader header, string frameId, byte[] jpeg, CancellationToken ct)
        => client.PostScreenshotAsync(header, frameId, jpeg, ct);
}

internal sealed record ScreenFrameItem(ScreenFrameHeader Header, string FrameId, byte[] Jpeg);

/// <summary>
/// Bounded, drop-oldest queue of periodic screenshots waiting to upload. Images are lower value than events,
/// so under backlog the oldest frame is discarded rather than retried forever; events are never blocked.
/// </summary>
public sealed class ScreenshotUploader(IScreenshotUpload upload, int capacity = 32)
{
    private readonly object _gate = new();
    private readonly Queue<ScreenFrameItem> _queue = new();
    private readonly int _capacity = Math.Max(1, capacity);

    public int DroppedCount { get; private set; }

    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }

    public void Enqueue(ScreenFrameHeader header, string frameId, byte[] jpeg)
    {
        lock (_gate)
        {
            while (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                DroppedCount++;
            }
            _queue.Enqueue(new ScreenFrameItem(header, frameId, jpeg));
        }
    }

    /// <summary>Uploads one queued frame. Returns false when the queue is empty.</summary>
    public async Task<bool> UploadNextAsync(CancellationToken ct)
    {
        ScreenFrameItem? item;
        lock (_gate) item = _queue.Count > 0 ? _queue.Dequeue() : null;
        if (item is null) return false;
        return await upload.PostAsync(item.Header, item.FrameId, item.Jpeg, ct).ConfigureAwait(false);
    }

    /// <summary>Drains the queue continuously until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await UploadNextAsync(ct).ConfigureAwait(false))
                    await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(1000, ct).ConfigureAwait(false); }
        }
    }
}
