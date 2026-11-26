using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Capture;

namespace NISS.Monitor.Tests;

public class ScreenCaptureTests
{
    private static readonly GdiScreenCaptureService Service = new();

    [Fact]
    public void EnumerateMonitors_includes_primary_when_a_desktop_is_present()
    {
        var monitors = Service.EnumerateMonitors();

        Assert.NotNull(monitors);
        if (monitors.Count > 0) Assert.Contains(monitors, m => m.Primary);
    }

    [Fact]
    public async Task CaptureMonitorAsync_produces_jpeg_within_maxwidth()
    {
        var monitors = Service.EnumerateMonitors();
        if (monitors.Count == 0) return; // headless agent host: nothing to capture

        var frame = await Service.CaptureMonitorAsync(0, maxWidth: 640, quality: 50, default);

        Assert.NotNull(frame);
        Assert.True(frame!.Jpeg.Length > 0);
        Assert.True(frame.Width <= 640);
        Assert.Equal(0xFF, frame.Jpeg[0]);
        Assert.Equal(0xD8, frame.Jpeg[1]); // JPEG start-of-image marker
    }

    [Fact]
    public async Task CaptureMonitorAsync_returns_null_for_unknown_monitor()
    {
        Assert.Null(await Service.CaptureMonitorAsync(999, 640, 50, default));
    }

    [Fact]
    public async Task CaptureMonitorAsync_minus_one_captures_the_whole_virtual_desktop()
    {
        if (Service.EnumerateMonitors().Count == 0) return; // headless: nothing to capture

        var frame = await Service.CaptureMonitorAsync(-1, maxWidth: 800, quality: 50, default);

        Assert.NotNull(frame);
        Assert.Equal(-1, frame!.MonitorIndex);
        Assert.True(frame.Jpeg.Length > 0);
        Assert.True(frame.Width <= 800);
    }
}
