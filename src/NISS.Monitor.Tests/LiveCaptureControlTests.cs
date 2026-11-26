using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Capture;
using NISS.Monitor.Infrastructure.Live;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public class LiveIndicatorTests
{
    [Fact]
    public void Indicator_show_then_hide_tracks_state()
    {
        var indicator = new LiveIndicatorOverlay(_ => new MonitorInfo(0, 0, 0, 100, 100, true), renderWindow: false);

        Assert.False(indicator.Visible);
        indicator.Show(0);
        Assert.True(indicator.Visible);
        Assert.Equal(0, indicator.Monitor);
        indicator.Hide();
        Assert.False(indicator.Visible);
        Assert.Equal(-1, indicator.Monitor);
    }
}

public class LiveCaptureControlTests
{
    private sealed class FakeCapture : IScreenCaptureService
    {
        public bool IsEnabled => true;
        public Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureMode mode, string reason, CancellationToken ct) => Task.FromResult(ScreenCaptureResult.Disabled);
        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => [new MonitorInfo(0, 0, 0, 100, 100, true)];
        public Task<ScreenCaptureFrame?> CaptureMonitorAsync(int monitorIndex, int maxWidth, int quality, CancellationToken ct)
            => Task.FromResult<ScreenCaptureFrame?>(new ScreenCaptureFrame(new byte[] { 1, 2, 3 }, 100, 100, monitorIndex));
    }

    private sealed class FakeIndicator : ILiveIndicator
    {
        public bool Visible { get; private set; }
        public int Monitor { get; private set; } = -1;
        public void Show(int monitor) { Visible = true; Monitor = monitor; }
        public void Hide() { Visible = false; Monitor = -1; }
    }

    [Fact]
    public void Live_start_shows_indicator_and_stop_hides_it()
    {
        var indicator = new FakeIndicator();
        var controller = new LiveCaptureController(new FakeCapture(), indicator,
            (_, _, _, _) => Task.CompletedTask, new FakeTimeProvider());

        controller.Start(new LiveControlMessage(LiveControlAction.Start, Guid.NewGuid(), Monitor: 1, Fps: 6, Quality: 50));
        Assert.True(indicator.Visible);
        Assert.Equal(1, indicator.Monitor);

        controller.Stop();
        Assert.False(indicator.Visible);
    }

    [Fact]
    public async Task Live_tick_captures_and_emits_a_frame_for_the_selected_monitor()
    {
        var emitted = new ConcurrentBag<(Guid Session, int Monitor, int Bytes)>();
        var controller = new LiveCaptureController(new FakeCapture(), new FakeIndicator(),
            (s, m, b, _) => { emitted.Add((s, m, b.Length)); return Task.CompletedTask; }, new FakeTimeProvider());
        controller.Switch(2); // select monitor without starting the streaming loop
        var session = Guid.NewGuid();

        var ok = await controller.TickAsync(session, quality: 50, default);

        Assert.True(ok);
        Assert.Contains(emitted, e => e.Session == session && e.Monitor == 2 && e.Bytes == 3);
    }

    [Fact]
    public void Live_stop_hides_indicator_on_disconnect()
    {
        var indicator = new FakeIndicator();
        var controller = new LiveCaptureController(new FakeCapture(), indicator,
            (_, _, _, _) => Task.CompletedTask, new FakeTimeProvider());
        controller.Start(new LiveControlMessage(LiveControlAction.Start, Guid.NewGuid(), 0, 6, 50));

        controller.Stop(); // disconnect/timeout path calls Stop()

        Assert.False(indicator.Visible);
    }
}
