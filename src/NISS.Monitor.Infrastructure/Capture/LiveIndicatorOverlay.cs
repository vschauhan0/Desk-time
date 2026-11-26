using System.Runtime.Versioning;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Capture;

/// <summary>Small on-screen "being viewed" notice shown to the employee only while a live session is active.</summary>
public interface ILiveIndicator
{
    void Show(int monitor);
    void Hide();
    bool Visible { get; }
}

/// <summary>No-op indicator: live view shows nothing on the watched screen. Swap for <see cref="LiveIndicatorOverlay"/> to re-enable the notice.</summary>
public sealed class NullLiveIndicator : ILiveIndicator
{
    public void Show(int monitor) { }
    public void Hide() { }
    public bool Visible => false;
}

/// <summary>
/// A small, always-on-top, click-through (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE) notice that the screen
/// is being viewed. Rendered on a dedicated message-pump thread; tracked by a simple state machine so it is testable
/// without a desktop (pass <c>renderWindow: false</c>). The employee cannot dismiss it; it is tied to the live session.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LiveIndicatorOverlay(Func<int, MonitorInfo?> monitorResolver, bool renderWindow = true) : ILiveIndicator, IDisposable
{
    private const string Text = "●  Screen being viewed by administrator";
    private readonly object _gate = new();
    private CancellationTokenSource? _windowCts;

    public bool Visible { get; private set; }
    public int Monitor { get; private set; } = -1;

    public void Show(int monitor)
    {
        lock (_gate)
        {
            if (Visible && Monitor == monitor) return;
            HideCore();
            Monitor = monitor;
            Visible = true;
            if (renderWindow) StartWindow(monitor);
        }
    }

    public void Hide()
    {
        lock (_gate) HideCore();
    }

    private void HideCore()
    {
        Visible = false;
        Monitor = -1;
        // Cancelling makes the pump thread destroy its window and exit, even if it has not finished creating it yet
        // (no lingering indicator, no leaked thread).
        try { _windowCts?.Cancel(); } catch { /* best effort */ }
        _windowCts = null;
    }

    private void StartWindow(int monitor)
    {
        var bounds = monitorResolver(monitor);
        if (bounds is null) return;
        var cts = new CancellationTokenSource();
        _windowCts = cts;
        var thread = new Thread(() => RunWindow(bounds, cts.Token)) { IsBackground = true, Name = "niss-live-indicator" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void RunWindow(MonitorInfo m, CancellationToken ct)
    {
        const int w = 360, h = 30;
        var x = m.X + (m.Width - w) / 2;
        var y = m.Y + 12;
        var ex = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOPMOST
                 | NativeMethods.WS_EX_NOACTIVATE | (uint)NativeMethods.WS_EX_TOOLWINDOW;
        var style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.SS_CENTER;
        IntPtr hwnd;
        try
        {
            hwnd = NativeMethods.CreateWindowExW(ex, "STATIC", Text, style, x, y, w, h,
                IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 220, NativeMethods.LWA_ALPHA);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { return; }

        // Pump messages while the session is live; a cancellation (Hide/Stop/Switch) ends the loop and destroys the window.
        while (!ct.IsCancellationRequested)
        {
            while (NativeMethods.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            Thread.Sleep(30);
        }
        try { NativeMethods.DestroyWindow(hwnd); } catch { /* ignore */ }
    }

    public void Dispose() => Hide();
}
