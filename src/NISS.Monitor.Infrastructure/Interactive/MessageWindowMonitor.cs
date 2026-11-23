using System.Runtime.InteropServices;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Interactive;

/// <summary>
/// One hidden window on a dedicated thread that provides (a) keyboard/mouse activity COUNTS via Raw Input and
/// (b) lock/unlock via WTS session notifications and (c) the end-of-session signal.
///
/// Privacy by construction: for keyboard input only the make/break flag and the message type are read; the virtual
/// key and scan code fields of the RAWKEYBOARD structure are never accessed, so there is nothing to store or leak.
/// No global hooks and no code injection are used.
/// </summary>
public sealed class MessageWindowMonitor : IInputActivityMonitor, ISessionLockMonitor
{
    private const string ClassName = "NISSMonitorSessionMessageWindow";
    private const uint RI_KEY_BREAK = 1;
    private const uint MOUSE_LEFT_DOWN = 0x0001, MOUSE_RIGHT_DOWN = 0x0004, MOUSE_MIDDLE_DOWN = 0x0010, MOUSE_WHEEL = 0x0400, MOUSE_HWHEEL = 0x0800;

    private readonly NativeMethods.WndProc _wndProc; // keep the delegate alive for the lifetime of the window
    private InputBucketAggregator? _aggregator;
    private IntPtr _hwnd;
    private volatile bool _locked;

    public MessageWindowMonitor() => _wndProc = WindowProc;

    /// <inheritdoc />
    public event Action<bool>? LockStateChanged;

    /// <summary>Raised when Windows announces logoff/shutdown of this session.</summary>
    public event Action? SessionEnding;

    /// <inheritdoc />
    public bool IsLocked => _locked;

    /// <inheritdoc />
    public Task RunAsync(InputBucketAggregator aggregator, CancellationToken cancellationToken)
    {
        _aggregator = aggregator;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Pump(cancellationToken); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        { IsBackground = true, Name = "NISS.MessagePump" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private void Pump(CancellationToken ct)
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        NativeMethods.RegisterClassExW(ref wc);
        _hwnd = NativeMethods.CreateWindowExW(0, ClassName, "NISS Monitor Session", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"Message window could not be created (error {Marshal.GetLastWin32Error()}).");

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _hwnd }, // mouse
            new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _hwnd }, // keyboard
        };
        if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"Raw input registration failed (error {Marshal.GetLastWin32Error()}).");
        NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION);

        using var stop = ct.Register(() => NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
        try
        {
            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                NativeMethods.DispatchMessageW(ref msg);
        }
        finally
        {
            NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_INPUT:
                try { HandleRawInput(lParam); } catch { /* never let a counting error break the pump */ }
                break;

            case NativeMethods.WM_WTSSESSION_CHANGE:
                var code = wParam.ToInt32();
                if (code == NativeMethods.WTS_SESSION_LOCK) { _locked = true; LockStateChanged?.Invoke(true); }
                else if (code == NativeMethods.WTS_SESSION_UNLOCK) { _locked = false; LockStateChanged?.Invoke(false); }
                return IntPtr.Zero;

            case NativeMethods.WM_QUERYENDSESSION:
                return new IntPtr(1);

            case NativeMethods.WM_ENDSESSION:
                if (wParam != IntPtr.Zero) SessionEnding?.Invoke();
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private unsafe void HandleRawInput(IntPtr hRawInput)
    {
        var agg = _aggregator;
        if (agg is null) return;

        var header = (uint)(IntPtr.Size == 8 ? 24 : 16);
        var size = 0u;
        NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0 || size > 96) return;

        var buffer = stackalloc byte[96];
        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, (IntPtr)buffer, ref size, header) != size) return;

        var type = *(uint*)buffer;
        var d = (int)header;
        if (type == NativeMethods.RIM_TYPEKEYBOARD)
        {
            // Only the break flag and message type are read. VKey / MakeCode are deliberately never touched.
            var flags = *(ushort*)(buffer + d + 2);
            var message = *(uint*)(buffer + d + 8);
            if ((flags & RI_KEY_BREAK) == 0 && (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)) agg.RecordKeyDown();
        }
        else if (type == NativeMethods.RIM_TYPEMOUSE)
        {
            var buttons = *(ushort*)(buffer + d + 4);
            var lastX = *(int*)(buffer + d + 12);
            var lastY = *(int*)(buffer + d + 16);
            if ((buttons & (MOUSE_LEFT_DOWN | MOUSE_RIGHT_DOWN | MOUSE_MIDDLE_DOWN)) != 0)
            {
                NativeMethods.GetCursorPos(out var p);
                agg.RecordClick(p.X, p.Y);
            }
            if ((buttons & (MOUSE_WHEEL | MOUSE_HWHEEL)) != 0) agg.RecordWheel();
            if (lastX != 0 || lastY != 0) agg.RecordMouseMove();
        }
    }
}

/// <summary>Reads the current cursor position (used only when cursor sampling is enabled by policy).</summary>
public static class CursorReader
{
    /// <summary>Current cursor position in screen coordinates.</summary>
    public static (int X, int Y)? TryGet() => NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : null;

    /// <summary>System double-click interval.</summary>
    public static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime());
}
