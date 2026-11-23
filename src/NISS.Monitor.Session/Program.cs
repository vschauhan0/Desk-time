using System.Diagnostics;
using NISS.Monitor.Core;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Interactive;
using NISS.Monitor.Infrastructure.Ipc;

namespace NISS.Monitor.Session;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Singleton per Windows session: the service and the HKLM Run key may both try to start us.
        using var mutex = new Mutex(true, AgentConstants.SessionMutexName, out var created);
        if (!created) return 0;

        using var log = new SessionLog();
        var sessionId = Process.GetCurrentProcess().SessionId;
        var user = Environment.UserName;
        log.Info($"NISS session agent {AgentVersion.Current} starting (session {sessionId}, user {user})");

        using var cts = new CancellationTokenSource();
        var window = new MessageWindowMonitor();
        window.SessionEnding += () => cts.Cancel(); // Windows is logging off / shutting down: flush and exit
        var pipe = new SessionPipeClient(user, AgentVersion.Current);
        var runtime = new SessionRuntime(new Win32ForegroundMonitor(), new Win32IdleMonitor(), window, pipe, TimeProvider.System, log, sessionId, user);

        try
        {
            runtime.RunAsync(cts.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Fatal: " + ex);
            return 1; // the service relaunches us with back-off
        }
    }
}
