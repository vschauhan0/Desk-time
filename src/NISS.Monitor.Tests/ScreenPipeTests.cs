using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Infrastructure.Live;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class ScreenPipeTests
{
    [Fact]
    public async Task ScreenPipe_round_trips_binary_frame()
    {
        var pipeName = "NISS-Screen-Test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<(ScreenFrameHeader Header, byte[] Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        var header = new ScreenFrameHeader(ScreenPipeKind.Live, Monitor: 1, Width: 100, Height: 50, CapturedAtUtcTicks: 123456, ByteLength: payload.Length);

        await using var server = new ScreenPipeServer(pipeName, requireTrustedClientPath: false,
            (h, bytes, _) => { received.TrySetResult((h, bytes)); return Task.CompletedTask; });
        _ = server.RunAsync(cts.Token);

        await using var client = new ScreenPipeClient(pipeName);
        await client.ConnectAsync(cts.Token);
        await client.SendFrameAsync(header, payload, cts.Token);

        var got = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(header, got.Header);
        Assert.Equal(payload, got.Payload);
    }
}

public class ScreenshotUploaderTests
{
    private sealed class FakeUpload : IScreenshotUpload
    {
        public readonly List<string> Posted = new();
        public Task<bool> PostAsync(ScreenFrameHeader header, string frameId, byte[] jpeg, CancellationToken ct)
        {
            Posted.Add(frameId);
            return Task.FromResult(true);
        }
    }

    private static ScreenFrameHeader H() => new(ScreenPipeKind.Periodic, 0, 10, 10, 0, 3);

    [Fact]
    public async Task Uploader_posts_frame_to_server()
    {
        var fake = new FakeUpload();
        var uploader = new ScreenshotUploader(fake, capacity: 8);
        uploader.Enqueue(H(), "f1", new byte[] { 1, 2, 3 });

        Assert.True(await uploader.UploadNextAsync(default));
        Assert.Equal(new[] { "f1" }, fake.Posted);
    }

    [Fact]
    public async Task Uploader_drops_oldest_when_full()
    {
        var fake = new FakeUpload();
        var uploader = new ScreenshotUploader(fake, capacity: 2);

        uploader.Enqueue(H(), "f1", new byte[] { 1 });
        uploader.Enqueue(H(), "f2", new byte[] { 1 });
        uploader.Enqueue(H(), "f3", new byte[] { 1 }); // overflows: f1 dropped

        Assert.Equal(2, uploader.Count);
        Assert.Equal(1, uploader.DroppedCount);
        await uploader.UploadNextAsync(default);
        await uploader.UploadNextAsync(default);
        Assert.Equal(new[] { "f2", "f3" }, fake.Posted);
    }
}
