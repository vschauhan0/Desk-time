using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Capture;

/// <summary>
/// Real screen capture for the interactive session process: GDI BitBlt (<see cref="Graphics.CopyFromScreen(int,int,int,int,Size)"/>)
/// per monitor, downscaled and JPEG-encoded. Light enough for a few frames per second.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    public bool IsEnabled => true;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        var index = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
            {
                var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfoW(hMon, ref mi))
                {
                    var r = mi.rcMonitor;
                    var primary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                    list.Add(new MonitorInfo(index++, r.left, r.top, r.right - r.left, r.bottom - r.top, primary));
                }
                return true;
            }, IntPtr.Zero);
        return list;
    }

    public async Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureMode mode, string reason, CancellationToken cancellationToken)
    {
        var frame = await CaptureMonitorAsync(0, 1600, 60, cancellationToken);
        return new ScreenCaptureResult(frame is not null, frame is null ? "No monitor to capture." : $"Captured monitor 0 ({reason}).");
    }

    public Task<ScreenCaptureFrame?> CaptureMonitorAsync(int monitorIndex, int maxWidth, int quality, CancellationToken cancellationToken)
    {
        var monitors = EnumerateMonitors();
        // -1 = the whole virtual desktop (used for periodic stills so every monitor is in the record).
        if (monitorIndex == -1)
        {
            if (monitors.Count == 0) return Task.FromResult<ScreenCaptureFrame?>(null);
            var left = monitors.Min(m => m.X);
            var top = monitors.Min(m => m.Y);
            var right = monitors.Max(m => m.X + m.Width);
            var bottom = monitors.Max(m => m.Y + m.Height);
            return Task.FromResult(Capture(new MonitorInfo(-1, left, top, right - left, bottom - top, false), maxWidth, quality));
        }
        if (monitorIndex < 0 || monitorIndex >= monitors.Count) return Task.FromResult<ScreenCaptureFrame?>(null);
        return Task.FromResult(Capture(monitors[monitorIndex], maxWidth, quality));
    }

    private static ScreenCaptureFrame? Capture(MonitorInfo m, int maxWidth, int quality)
    {
        if (m.Width <= 0 || m.Height <= 0) return null;
        try
        {
            using var shot = new Bitmap(m.Width, m.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen(m.X, m.Y, 0, 0, new Size(m.Width, m.Height), CopyPixelOperation.SourceCopy);

            var (outW, outH) = Scale(m.Width, m.Height, maxWidth);
            using Image image = (outW == m.Width && outH == m.Height) ? shot : new Bitmap(shot, new Size(outW, outH));
            using var ms = new MemoryStream();
            image.Save(ms, JpegEncoder, QualityParams(quality));
            return new ScreenCaptureFrame(ms.ToArray(), outW, outH, m.Index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (int Width, int Height) Scale(int w, int h, int maxWidth)
    {
        if (maxWidth <= 0 || w <= maxWidth) return (w, h);
        var ratio = (double)maxWidth / w;
        return (maxWidth, Math.Max(1, (int)(h * ratio)));
    }

    private static readonly ImageCodecInfo JpegEncoder =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static EncoderParameters QualityParams(int quality)
    {
        var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        return p;
    }
}
