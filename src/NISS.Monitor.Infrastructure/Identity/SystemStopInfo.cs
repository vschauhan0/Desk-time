using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Identity;

/// <summary>
/// Win32 inputs for classifying a service stop (see <c>StopClassifier</c>): whether the OS is shutting down, and who
/// is at the console. Thin by design - the decision logic is the pure, tested <c>StopClassifier</c>; this only reads
/// the machine state and is verified on a real elevated PC, not in unit tests.
/// </summary>
public static class SystemStopInfo
{
    private const int SM_SHUTTINGDOWN = 0x2000;

    /// <summary>True when Windows is shutting down or restarting (so a stop at this moment is benign, not tamper).</summary>
    public static bool IsShuttingDown()
    {
        try { return NativeMethods.GetSystemMetrics(SM_SHUTTINGDOWN) != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>The <c>DOMAIN\user</c> signed in at the physical console, or null when nobody is (headless / our own tooling).</summary>
    public static string? ConsoleUser()
    {
        try
        {
            var id = NativeMethods.WTSGetActiveConsoleSessionId();
            if (id == 0xFFFFFFFF) return null;
            var user = WtsQuery.QueryString((int)id, NativeMethods.WTSUserName);
            if (string.IsNullOrEmpty(user)) return null;
            var domain = WtsQuery.QueryString((int)id, NativeMethods.WTSDomainName);
            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
