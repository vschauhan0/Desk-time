using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Collectors;
using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Ipc;

/// <summary>
/// Keeps a NISSMonitor.Session.exe running in every interactive session. Uses the documented
/// WTSQueryUserToken + CreateProcessAsUser pattern (LocalSystem service). If the process dies it is restarted with
/// back-off; a startup Run key installed by the installer is a second, harmless path (the session process is a singleton).
/// </summary>
public sealed class SessionLauncher : ICollector
{
    private readonly AgentSettings _settings;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionLauncher> _log;
    private readonly Dictionary<int, LaunchState> _launches = [];
    private readonly BackoffPolicy _backoff = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

    private sealed class LaunchState
    {
        public DateTimeOffset FirstMissing;
        public DateTimeOffset NextAttempt;
        public int Attempts;
    }

    public SessionLauncher(IOptions<AgentSettings> settings, ICollectorRegistry registry, TimeProvider time, ILogger<SessionLauncher> log)
    {
        _settings = settings.Value; _registry = registry; _time = time; _log = log;
    }

    public string Name => "SessionLauncher";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Session.LaunchSessionAgents)
        {
            _registry.Report(Name, "Disabled");
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (Environment.UserInteractive)
        {
            _log.LogInformation("Running as a console host: session agents are not launched automatically (start NISSMonitor.Session.exe manually)");
            _registry.Report(Name, "Disabled", "console host");
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }

        _registry.Report(Name, "Running");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try { Tick(); _registry.Touch(Name); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                _log.LogWarning(ex, "Session launcher tick failed");
                _registry.Report(Name, "Degraded", ex.Message);
            }
        }
    }

    private void Tick()
    {
        var now = _time.GetUtcNow();
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AgentConstants.SessionExeName))
            .Select(p => { try { return NativeMethods.GetProcessSessionId(p.Id); } finally { p.Dispose(); } })
            .ToHashSet();

        var sessions = WtsSessionMonitor.Enumerate().Where(s => s.State is "Active" or "Connected").ToList();
        foreach (var s in sessions)
        {
            if (running.Contains(s.SessionId)) { _launches.Remove(s.SessionId); continue; }
            if (!_launches.TryGetValue(s.SessionId, out var st))
                _launches[s.SessionId] = st = new LaunchState { FirstMissing = now, NextAttempt = now.AddSeconds(8) }; // grace for the Run key
            if (now < st.NextAttempt) continue;

            if (TryLaunch(s.SessionId)) _log.LogInformation("Launched session agent in session {SessionId} for {User}", s.SessionId, s.UserName);
            else _log.LogWarning("Could not launch session agent in session {SessionId} (attempt {Attempt})", s.SessionId, st.Attempts + 1);
            st.Attempts++;
            st.NextAttempt = now + _backoff.Next(st.Attempts);
        }
        foreach (var gone in _launches.Keys.Where(k => sessions.All(s => s.SessionId != k)).ToList()) _launches.Remove(gone);
    }

    private bool TryLaunch(int sessionId)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, AgentConstants.SessionExeName);
        if (!File.Exists(exe)) { _log.LogError("Session agent not found at {Path}", exe); return false; }

        IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out userToken)) return Fail("WTSQueryUserToken");
            if (!NativeMethods.DuplicateTokenEx(userToken, NativeMethods.TOKEN_ALL_ACCESS, IntPtr.Zero, NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out primary))
                return Fail("DuplicateTokenEx");
            NativeMethods.CreateEnvironmentBlock(out env, primary, false);

            var si = new NativeMethods.STARTUPINFOW { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STARTUPINFOW>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{exe}\"");
            if (!NativeMethods.CreateProcessAsUserW(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false, NativeMethods.CREATE_UNICODE_ENVIRONMENT, env, AppContext.BaseDirectory, ref si, out var pi))
                return Fail("CreateProcessAsUser");
            NativeMethods.CloseHandle(pi.hProcess);
            NativeMethods.CloseHandle(pi.hThread);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) NativeMethods.DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) NativeMethods.CloseHandle(primary);
            if (userToken != IntPtr.Zero) NativeMethods.CloseHandle(userToken);
        }

        bool Fail(string api)
        {
            _log.LogWarning("{Api} failed with Win32 error {Error}", api, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }
    }
}
