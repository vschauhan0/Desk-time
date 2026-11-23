using System.Text;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Interactive;

/// <summary>Foreground window / open-application discovery using GetForegroundWindow, GetWindowThreadProcessId, GetWindowText, EnumWindows.</summary>
public sealed class Win32ForegroundMonitor : IForegroundMonitor
{
    private const int MaxTitle = 256;
    private readonly int _ownPid = Environment.ProcessId;

    /// <inheritdoc />
    public ForegroundSample? Sample(bool captureTitle)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var (pid, resolvedHwnd) = ResolveOwner(hwnd);
        if (pid <= 0 || pid == _ownPid) return null;
        var path = NativeMethods.GetProcessImagePath(pid);
        if (path is null) return null; // protected/system windows (e.g. secure desktop) are not attributed to an app

        return new ForegroundSample(pid, Path.GetFileName(path), path, captureTitle ? Title(hwnd, resolvedHwnd) : null);
    }

    /// <inheritdoc />
    public IReadOnlyList<OpenWindowApp> EnumerateOpenApplications(bool captureTitle)
    {
        var found = new Dictionary<int, OpenWindowApp>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                var owned = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero;
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & NativeMethods.WS_EX_APPWINDOW) == 0) return true;
                if (owned && (ex & NativeMethods.WS_EX_APPWINDOW) == 0) return true;
                if (NativeMethods.GetWindowTextLengthW(hwnd) == 0) return true;
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

                var cls = ClassName(hwnd);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

                var (pid, resolved) = ResolveOwner(hwnd);
                if (pid <= 0 || pid == _ownPid || found.ContainsKey(pid)) return true;
                var path = NativeMethods.GetProcessImagePath(pid);
                if (path is null) return true;
                found[pid] = new OpenWindowApp(pid, Path.GetFileName(path), path, captureTitle ? Title(hwnd, resolved) : null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return found.Values.ToList();
    }

    /// <summary>
    /// Modern (UWP) apps are hosted in ApplicationFrameHost.exe; the real app is the process that owns the
    /// "Windows.UI.Core.CoreWindow" child. Everything else maps window -> owning process directly.
    /// </summary>
    private static (int Pid, IntPtr Hwnd) ResolveOwner(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var raw);
        var pid = (int)raw;
        if (pid <= 0 || ClassName(hwnd) != "ApplicationFrameWindow") return (pid, hwnd);

        var real = 0;
        var realHwnd = hwnd;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(child, out var cp);
            if ((int)cp != pid && cp != 0) { real = (int)cp; realHwnd = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return (real > 0 ? real : pid, realHwnd);
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string? Title(IntPtr hwnd, IntPtr fallback)
    {
        var len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0 && fallback != hwnd) { hwnd = fallback; len = NativeMethods.GetWindowTextLengthW(hwnd); }
        if (len <= 0) return null;
        var sb = new StringBuilder(Math.Min(len + 1, MaxTitle));
        var n = NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString().Trim() : null;
    }
}

/// <summary>User idle time from GetLastInputInfo (system-wide keyboard/mouse/touch input).</summary>
public sealed class Win32IdleMonitor : IIdleMonitor
{
    /// <inheritdoc />
    public TimeSpan GetIdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime); // uint arithmetic survives the 49.7-day tick wrap
        return TimeSpan.FromMilliseconds(elapsedMs);
    }
}

/// <summary>True when the user's input desktop is the normal desktop (false on the lock screen / secure desktop).</summary>
public static class DesktopState
{
    /// <summary>Returns false while the workstation is locked or a UAC/secure desktop is showing.</summary>
    public static bool IsDefaultDesktopActive()
    {
        var h = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_SWITCHDESKTOP);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(64);
            return !NativeMethods.GetUserObjectInformationW(h, NativeMethods.UOI_NAME, sb, sb.Capacity * 2, out _)
                   || string.Equals(sb.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { NativeMethods.CloseDesktop(h); }
    }
}
