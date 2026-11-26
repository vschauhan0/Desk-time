using Microsoft.Extensions.Time.Testing;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Live;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class CaptureGatingTests
{
    private sealed class FakeCapture : IScreenCaptureService
    {
        public int Calls;
        public bool IsEnabled => true;
        public Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureMode mode, string reason, CancellationToken ct) => Task.FromResult(ScreenCaptureResult.Disabled);
        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => [new MonitorInfo(0, 0, 0, 100, 100, true)];
        public Task<ScreenCaptureFrame?> CaptureMonitorAsync(int monitorIndex, int maxWidth, int quality, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<ScreenCaptureFrame?>(new ScreenCaptureFrame(new byte[] { 1, 2, 3 }, 100, 100, monitorIndex));
        }
    }

    private static ScreenCaptureLoop Loop(FakeCapture capture, ScreenCapturePolicy policy, PresenceSnapshot presence, List<ScreenFrameHeader> sink) =>
        new(capture, () => policy, () => presence,
            (h, _, _) => { sink.Add(h); return Task.CompletedTask; }, new FakeTimeProvider());

    [Fact]
    public async Task Capture_loop_noops_when_disabled()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var loop = Loop(capture, new ScreenCapturePolicy(Enabled: false, 60, 60, 1600, true), new PresenceSnapshot(false, false), sink);

        var captured = await loop.TickAsync(default);

        Assert.False(captured);
        Assert.Equal(0, capture.Calls);
        Assert.Empty(sink);
    }

    [Fact]
    public async Task Capture_loop_noops_when_locked()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var loop = Loop(capture, new ScreenCapturePolicy(Enabled: true, 60, 60, 1600, true), new PresenceSnapshot(Locked: true, Idle: false), sink);

        var captured = await loop.TickAsync(default);

        Assert.False(captured);
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public async Task Capture_loop_captures_when_enabled_and_active()
    {
        var capture = new FakeCapture();
        var sink = new List<ScreenFrameHeader>();
        var loop = Loop(capture, new ScreenCapturePolicy(Enabled: true, 60, 60, 1600, true), new PresenceSnapshot(false, false), sink);

        var captured = await loop.TickAsync(default);

        Assert.True(captured);
        Assert.Equal(1, capture.Calls);
        Assert.Single(sink);
        Assert.Equal(ScreenPipeKind.Periodic, sink[0].Kind);
    }
}
