// This file is also compiled directly into NISS.Monitor.Installer and NISS.Monitor.Watchdog (linked), so it must
// stay dependency-free (BCL + Microsoft.Win32 only) and must not reference other NISS projects.
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace NISS.Monitor.Setup;

/// <summary>Result of running an external tool.</summary>
public sealed record ToolResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Everything needed to install, repair or remove the agent using standard, documented Windows mechanisms
/// (Service Control Manager, registry Run key, ACLs). Nothing here hides the agent: both services are visible in
/// services.msc, the files live in Program Files, and administrators can stop, disable or uninstall them normally.
///
/// Two services are installed as a mutual watchdog pair: <see cref="ServiceName"/> (the real agent) and
/// <see cref="WatchdogServiceName"/> (a small companion whose only job is restarting the other one if it stops).
/// Each watches the other, so a casual stop of either one (Task Manager, Services.msc, <c>sc stop</c>) is corrected
/// automatically within a few seconds - see <c>PeerServiceWatchdog</c> for exactly what this does and does not
/// protect against. This is ordinary, documented Windows service behaviour, not tamper protection: an administrator
/// who wants to stop monitoring permanently still can, by disabling (or uninstalling) both services.
/// </summary>
public static class AgentSetup
{
    public const string ServiceName = "NISSMonitorService";
    public const string DisplayName = "NISS Employee Monitoring Service";
    public const string Description = "Authorized NISS endpoint telemetry service for managed organizational devices.";
    public const string EventSource = "NISS Monitor Service";
    public const string ServiceExe = "Windows.RunService.exe";

    public const string WatchdogServiceName = "Watchdog";
    public const string WatchdogDisplayName = "Watchdog";
    public const string WatchdogDescription = "Keeps the NISS Employee Monitoring Service running. Authorized NISS endpoint component for managed organizational devices.";
    public const string WatchdogEventSource = "NISS Monitor Watchdog";
    public const string WatchdogExe = "Windows.RunWatchdog.exe";

    public const string RunValueName = "NISSMonitorSession";
    public const string SessionExe = "Windows.RunSession.exe";
    public const string CliExe = "NISSAgent.exe";
    public static string DefaultInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NISS", "Monitor");
    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NISS", "Agent");

    // --- Service DACL hardening (layer-2 anti-tamper) ---------------------------------------------------------------
    // Security descriptors applied to each service with `sc sdset`. DACL-only on purpose: a SACL would need
    // SeSecurityPrivilege, we only care who may STOP/DELETE, and `sc sdset` leaves any existing SACL untouched.
    // Rights letters: CC=query-config DC=change-config LC=query-status SW=enum-deps RP=START WP=STOP DT=pause
    //                 LO=interrogate CR=user-defined-control SD=DELETE RC=read-control WD=write-DAC WO=write-owner.

    /// <summary>What Windows grants a new service: SYSTEM + Administrators full control. Used to undo hardening.</summary>
    public const string DefaultServiceSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    /// <summary>Hardened DACL: Administrators lose WP (STOP) and SD (DELETE), so Services.msc, Task Manager,
    /// <c>net stop</c>, <c>sc stop</c> and <c>sc delete</c> are denied even to an admin. We deliberately KEEP DC
    /// (change-config) and WD (write-DAC) so the official uninstaller/repair - and a determined admin - can still
    /// re-assert control: this is a speed bump plus detection (layer 3), not a wall. SYSTEM keeps full control so SCM
    /// recovery actions and the peer watchdog keep working.</summary>
    public const string HardenedServiceSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPDTLOCRRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    /// <summary>Run a console tool and capture its output.</summary>
    public static ToolResult Run(string file, string arguments, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } return new ToolResult(-1, "timed out"); }
        return new ToolResult(p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    /// <summary>True when the named service is registered with the SCM.</summary>
    public static bool ServiceExists(string serviceName = ServiceName)
        => ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Current state of the named service, or "NotInstalled".</summary>
    public static string ServiceState(string serviceName = ServiceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status.ToString();
        }
        catch (InvalidOperationException) { return "NotInstalled"; }
    }

    /// <summary>Create (or reconfigure) the main agent service and its recovery actions. When <paramref name="hardenAcl"/>
    /// is true (default) the service ends with the hardened DACL; false actively restores the default DACL.</summary>
    public static void InstallOrUpdateService(string installDir, Action<string> log, bool hardenAcl = true)
        => InstallOrUpdateService(ServiceName, DisplayName, Description, EventSource, Path.Combine(installDir, ServiceExe), log, hardenAcl);

    /// <summary>Create (or reconfigure) the watchdog service and its recovery actions.</summary>
    public static void InstallOrUpdateWatchdogService(string installDir, Action<string> log, bool hardenAcl = true)
        => InstallOrUpdateService(WatchdogServiceName, WatchdogDisplayName, WatchdogDescription, WatchdogEventSource, Path.Combine(installDir, WatchdogExe), log, hardenAcl);

    /// <summary>
    /// Create (or reconfigure) a service and its recovery actions. Recovery actions (restart on crash) and the
    /// mutual watchdog worker inside each service (restart on a deliberate stop) are complementary: this covers
    /// unexpected crashes, the watchdog covers Task Manager / <c>sc stop</c> / Services.msc.
    /// </summary>
    private static void InstallOrUpdateService(string serviceName, string displayName, string description, string eventSource, string bin, Action<string> log, bool hardenAcl)
    {
        if (!File.Exists(bin)) throw new FileNotFoundException("Service executable not found", bin);
        var quoted = $"\"{bin}\"";

        if (!ServiceExists(serviceName))
        {
            Check(Run("sc.exe", $"create {serviceName} binPath= {quoted} start= auto obj= LocalSystem DisplayName= \"{displayName}\""), "sc create");
            log($"{displayName} created");
        }
        else
        {
            Check(Run("sc.exe", $"config {serviceName} binPath= {quoted} start= auto obj= LocalSystem DisplayName= \"{displayName}\""), "sc config");
            log($"{displayName} reconfigured");
        }

        Check(Run("sc.exe", $"description {serviceName} \"{description}\""), "sc description");
        // restart on 1st, 2nd and every subsequent failure; failures counter resets after one day
        Check(Run("sc.exe", $"failure {serviceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"), "sc failure");
        Check(Run("sc.exe", $"failureflag {serviceName} 1"), "sc failureflag"); // also recover when the service exits with an error code
        log($"{displayName} recovery configured: restart / restart / restart");
        RegisterEventSource(eventSource);
        // Leave the service in the desired protection state. Hardening is the last step so it is applied on top of
        // the fresh registration; opting out actively clears any hardening a previous install left behind.
        if (hardenAcl) HardenServiceAcl(serviceName, log);
        else UnhardenServiceAcl(serviceName);
    }

    /// <summary>Apply the hardened service DACL (see <see cref="HardenedServiceSddl"/>) so administrators can no
    /// longer stop or delete the service with the usual tools. No-op if the service is not registered.</summary>
    public static void HardenServiceAcl(string serviceName = ServiceName, Action<string>? log = null)
    {
        if (!ServiceExists(serviceName)) return;
        Check(Run("sc.exe", $"sdset {serviceName} {HardenedServiceSddl}"), "sc sdset (harden)");
        log?.Invoke($"{serviceName} locked: administrators can no longer stop or delete it with the usual tools");
    }

    /// <summary>Argument string for <c>sc.exe</c> that repoints a service at a different executable (the side-by-side
    /// update switch). Only <c>binPath=</c> is touched: start type, account and recovery settings stay as installed.</summary>
    public static string ScConfigBinPathArgs(string serviceName, string exePath) => $"config {serviceName} binPath= \"{exePath}\"";

    /// <summary>Repoint <paramref name="serviceName"/> at <paramref name="exePath"/> and then RE-APPLY the hardened DACL.
    /// Never weakens the ACL: the caller (the SYSTEM watchdog) already holds change-config, so no unhardening is needed,
    /// and the DACL is asserted again afterwards so a reconfigure can never leave the service less protected.</summary>
    public static void SwitchServiceBinPath(string serviceName, string exePath, Action<string>? log = null)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("Service executable not found", exePath);
        Check(Run("sc.exe", ScConfigBinPathArgs(serviceName, exePath)), "sc config binPath");
        HardenServiceAcl(serviceName, log);
    }

    /// <summary>Restore the default service DACL (administrators full control). Best-effort - used by the agent's own
    /// elevated stop/delete/reconfigure paths so they keep working against an already-hardened service.</summary>
    public static void UnhardenServiceAcl(string serviceName = ServiceName)
    {
        if (!ServiceExists(serviceName)) return;
        try { Run("sc.exe", $"sdset {serviceName} {DefaultServiceSddl}"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Register an Application event-log source so a service can write to Windows Event Log.</summary>
    public static void RegisterEventSource(string eventSource = EventSource)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Services\EventLog\Application\{eventSource}");
        key.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", RegistryValueKind.ExpandString);
        key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
    }

    /// <summary>Start the session agent for every interactive logon (the service also launches it; the process is a singleton per session).</summary>
    public static void SetSessionRunKey(string installDir)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)
                        ?? throw new InvalidOperationException("Run key not available");
        key.SetValue(RunValueName, $"\"{Path.Combine(installDir, SessionExe)}\"", RegistryValueKind.String);
    }

    /// <summary>Remove the Run key entry.</summary>
    public static void RemoveSessionRunKey()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue(RunValueName, false);
    }

    /// <summary>
    /// Restrict the agent data folder (queue database, logs) to SYSTEM and Administrators. Deliberately NOT
    /// recursive (no /T): the installer's own log file already lives under this folder and is open for writing
    /// while this runs, and recursively re-touching an open file's ACL can corrupt it into an empty (deny-all)
    /// DACL. Setting the ACL only on the top-level folder is sufficient - NTFS inheritance (OI)(CI) applies it to
    /// every file/subfolder created from this point on (the database, future logs), which is everything that
    /// actually needs to be protected; anything already sitting here keeps working with its existing permissions.
    /// </summary>
    public static void SecureDataDirectory(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "Logs"));
        Check(Run("icacls.exe", $"\"{dataDir}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" /C"), "icacls");
    }

    /// <summary>Stop the named service (and wait) if it is running. Stop the watchdog before the main service when replacing files, or the watchdog will restart it mid-update.</summary>
    public static void StopService(TimeSpan timeout, string serviceName = ServiceName)
    {
        UnhardenServiceAcl(serviceName); // our own (elevated) stop needs SERVICE_STOP, which the hardened DACL denies
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) { sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout); return; }
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
        catch (InvalidOperationException) { /* not installed */ }
        catch (System.ServiceProcess.TimeoutException) { }
    }

    /// <summary>Start the named service and wait until it is running.</summary>
    public static void StartService(TimeSpan timeout, string serviceName = ServiceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return;
        }
        try { sc.Start(); }
        catch (InvalidOperationException) { sc.Refresh(); if (sc.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)) throw; }
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    /// <summary>End every running session agent so files can be replaced.</summary>
    public static void StopSessionAgents()
    {
        var names = new[] { Path.GetFileNameWithoutExtension(SessionExe), "NISSMonitor.Session", "NISSAgent" };
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); p.WaitForExit(5000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>Delete a service registration.</summary>
    public static void DeleteService(string serviceName = ServiceName)
    {
        if (!ServiceExists(serviceName)) return;
        UnhardenServiceAcl(serviceName); // DELETE is denied to admins by the hardened DACL; restore it first
        Check(Run("sc.exe", $"delete {serviceName}"), "sc delete");
    }

    private static void Check(ToolResult r, string what)
    {
        if (!r.Ok) throw new InvalidOperationException($"{what} failed (exit {r.ExitCode}): {r.Output}");
    }
}
