using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NISS.Monitor.Setup;

namespace NISS.Monitor.Installer;

/// <summary>
/// NISSMonitorSetup.exe - installer / repair / uninstaller for the NISS endpoint agent. Requires administrator rights
/// (manifest). Uses only standard Windows mechanisms and leaves everything visible to administrators.
///
///   NISSMonitorSetup.exe [/silent] --server https://192.168.1.10:8443 --token XXXX [--employee EMP1024] [--thumbprint HEX]
///   NISSMonitorSetup.exe --repair
///   NISSMonitorSetup.exe --uninstall [--purge]
/// </summary>
internal static class Program
{
    private const int MinBuild = 17763; // Windows 10 1809 / Server 2019

    private static int Main(string[] args)
    {
        var o = Options.Parse(args);
        var log = new InstallLog(AgentSetup.DefaultDataDir);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Banner(o);

        try
        {
            var result = o.Mode switch
            {
                Mode.Uninstall => new Steps(o, log).Uninstall(),
                Mode.Repair => new Steps(o, log).Install(repair: true),
                _ => new Steps(o, log).Install(repair: false),
            };
            Finish(o, result.Success, result.Summary);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            log.Write("FATAL " + ex);
            Finish(o, false, "Installation failed: " + ex.Message + "\nSee " + log.Path);
            return 1;
        }
    }

    private static void Banner(Options o)
    {
        Console.WriteLine("NISS Employee Monitoring Agent - Setup");
        Console.WriteLine("Authorized telemetry for organization-managed devices.");
        Console.WriteLine(o.Mode switch { Mode.Uninstall => "Mode: uninstall", Mode.Repair => "Mode: repair", _ => "Mode: install" });
        Console.WriteLine();
    }

    private static void Finish(Options o, bool ok, string summary)
    {
        Console.WriteLine();
        Console.WriteLine(ok ? "RESULT: SUCCESS" : "RESULT: FAILED");
        Console.WriteLine(summary);
        if (!o.Silent) NativeMessageBox.Show(summary, ok ? "NISS Monitor - Setup complete" : "NISS Monitor - Setup failed", ok);
    }
}

internal enum Mode { Install, Repair, Uninstall }

internal sealed class Options
{
    public Mode Mode { get; private set; } = Mode.Install;
    public bool Silent { get; private set; }
    public bool Purge { get; private set; }
    public bool SkipEnroll { get; private set; }
    public string InstallDir { get; private set; } = AgentSetup.DefaultInstallDir;
    public string? Server { get; set; }
    public string? Token { get; set; }
    public string? Employee { get; private set; }
    public string? Thumbprint { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].TrimStart('-', '/').ToLowerInvariant();
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "silent" or "s" or "quiet": o.Silent = true; break;
                case "uninstall" or "remove": o.Mode = Mode.Uninstall; break;
                case "repair": o.Mode = Mode.Repair; break;
                case "purge": o.Purge = true; break;
                case "no-enroll": o.SkipEnroll = true; break;
                case "server": o.Server = Next(); break;
                case "token": o.Token = Next(); break;
                case "employee": o.Employee = Next(); break;
                case "thumbprint": o.Thumbprint = Next(); break;
                case "install-dir": o.InstallDir = Next() ?? o.InstallDir; break;
            }
        }
        return o;
    }
}

internal sealed record StepResult(bool Success, string Summary);

/// <summary>The installation steps, each printed and logged.</summary>
internal sealed class Steps
{
    private readonly Options _o;
    private readonly InstallLog _log;
    private int _n;
    private readonly int _total;

    public Steps(Options o, InstallLog log)
    {
        _o = o;
        _log = log;
        _total = o.Mode == Mode.Uninstall ? 6 : 13;
    }

    public StepResult Install(bool repair)
    {
        Step("Verifying Windows version", () =>
        {
            var v = Environment.OSVersion.Version;
            if (v.Build < 17763) throw new InvalidOperationException($"Windows build {v.Build} is not supported (need {17763}+ = Windows 10 1809 / Server 2019 or newer).");
            if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("A 64-bit version of Windows is required.");
            return $"Windows {v}";
        });

        var haveInstalledFiles = File.Exists(Path.Combine(_o.InstallDir, AgentSetup.ServiceExe));
        var payload = PayloadSource.Find();
        if (!repair && payload is null) throw new InvalidOperationException("Installation payload not found (NISSMonitorPayload.zip). Rebuild the installer with deployment\\scripts\\Build-Agent.ps1.");
        if (repair && payload is null && !haveInstalledFiles) throw new InvalidOperationException("Nothing to repair: the agent is not installed and no payload is available.");

        Step("Stopping existing services and session agents", () =>
        {
            // Stop the watchdog FIRST - it would otherwise notice the main service going down and restart it
            // mid-file-replacement.
            AgentSetup.StopService(TimeSpan.FromSeconds(15), AgentSetup.WatchdogServiceName);
            AgentSetup.StopService(TimeSpan.FromSeconds(40));
            AgentSetup.StopSessionAgents();
            return AgentSetup.ServiceExists() ? "stopped" : "not previously installed";
        });

        Step("Installing files", () =>
        {
            Directory.CreateDirectory(_o.InstallDir);
            if (payload is null) return "kept existing files";
            var kept = ReadSettings();
            payload.ExtractTo(_o.InstallDir);
            if (kept is not null) File.WriteAllText(SettingsPath, kept.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); // never clobber site configuration
            return $"{_o.InstallDir}";
        });

        Step("Creating data directories and permissions", () =>
        {
            AgentSetup.SecureDataDirectory(AgentSetup.DefaultDataDir);
            return AgentSetup.DefaultDataDir + " (SYSTEM + Administrators only)";
        });

        Step("Writing configuration", () =>
        {
            var root = ReadSettings() ?? new JsonObject();
            var srv = Ensure(Ensure(root, "Agent"), "Server");
            if (!string.IsNullOrWhiteSpace(_o.Server)) srv["BaseUrl"] = _o.Server!.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(_o.Thumbprint)) srv["CertificateThumbprint"] = _o.Thumbprint;
            File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _o.Server ??= srv["BaseUrl"]?.GetValue<string>();
            _o.Thumbprint ??= srv["CertificateThumbprint"]?.GetValue<string>();
            return $"server {_o.Server}";
        });

        Step("Installing Windows service with recovery actions", () => { AgentSetup.InstallOrUpdateService(_o.InstallDir, _log.Write); return AgentSetup.ServiceName; });
        Step("Installing watchdog service", () =>
        {
            // The watchdog restarts the main service if anyone stops it (Task Manager, sc stop, Services.msc);
            // the main service runs the mirror-image check for the watchdog. Both stay ordinary, visible services.
            AgentSetup.InstallOrUpdateWatchdogService(_o.InstallDir, _log.Write);
            return AgentSetup.WatchdogServiceName;
        });
        Step("Registering session agent startup", () => { AgentSetup.SetSessionRunKey(_o.InstallDir); return "HKLM Run key + service launcher"; });

        var cli = Path.Combine(_o.InstallDir, AgentSetup.CliExe);
        Step("Creating local SQLite queue database", () => Cli(cli, "--init"));

        PromptForEnrollmentCode(cli);

        Step("Enrolling device with the NISS server", () =>
        {
            var enrolled = Cli(cli, "--device-id", tolerateFailure: true);
            var alreadyEnrolled = !enrolled.StartsWith("ERR:", StringComparison.Ordinal);
            if (_o.SkipEnroll || (alreadyEnrolled && string.IsNullOrWhiteSpace(_o.Token))) return alreadyEnrolled ? $"already enrolled as {enrolled}" : "skipped (--no-enroll)";
            if (string.IsNullOrWhiteSpace(_o.Token)) return "skipped - no enrollment code supplied; run: NISSAgent.exe --enroll --server <url> --token <code>";
            var args = $"--enroll --server \"{_o.Server}\" --token \"{_o.Token}\"" + (string.IsNullOrWhiteSpace(_o.Employee) ? "" : $" --employee \"{_o.Employee}\"")
                       + (string.IsNullOrWhiteSpace(_o.Thumbprint) ? "" : $" --thumbprint \"{_o.Thumbprint}\"");
            return Cli(cli, args);
        });

        Step("Starting services", () =>
        {
            AgentSetup.StartService(TimeSpan.FromSeconds(40));
            AgentSetup.StartService(TimeSpan.FromSeconds(20), AgentSetup.WatchdogServiceName);
            return "main service + watchdog running";
        });

        string health = "";
        Step("Validating communication with the server", () =>
        {
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                var r = AgentSetup.Run(cli, "--health", 30_000);
                health = r.Output;
                if (r.Ok) return r.Output.Replace("\r\n", " | ");
                if (r.ExitCode == 4) return "device not enrolled yet";
                Thread.Sleep(4000);
            }
            throw new InvalidOperationException("Server did not accept this device: " + health.Replace("\r\n", " | "));
        }, fatal: false);

        Step("Checking session agent", () =>
        {
            for (var i = 0; i < 15; i++)
            {
                if (System.Diagnostics.Process.GetProcessesByName("NISSMonitor.Session").Length > 0) return "running in the interactive session";
                Thread.Sleep(1000);
            }
            return "not running yet - it starts at the next user logon";
        }, fatal: false);

        var device = Cli(cli, "--device-id", tolerateFailure: true);
        var summary = $"NISS Monitor agent {(repair ? "repaired" : "installed")}.\nService: {AgentSetup.ServiceState()}\nWatchdog: {AgentSetup.ServiceState(AgentSetup.WatchdogServiceName)}\nDevice: {(device.StartsWith("ERR:", StringComparison.Ordinal) ? "not enrolled" : device)}\nServer: {_o.Server}\nInstall folder: {_o.InstallDir}\nLog: {_log.Path}";
        return new StepResult(true, summary);
    }

    public StepResult Uninstall()
    {
        Step("Stopping services", () =>
        {
            // Watchdog first, same reasoning as install: otherwise it restarts the main service right after we stop it.
            AgentSetup.StopService(TimeSpan.FromSeconds(15), AgentSetup.WatchdogServiceName);
            AgentSetup.StopService(TimeSpan.FromSeconds(40));
            return AgentSetup.ServiceState();
        });
        Step("Stopping session agents", () => { AgentSetup.StopSessionAgents(); return "done"; });
        Step("Removing service registration", () =>
        {
            AgentSetup.DeleteService(AgentSetup.WatchdogServiceName);
            AgentSetup.DeleteService();
            return "removed";
        });
        Step("Removing session agent startup entry", () => { AgentSetup.RemoveSessionRunKey(); return "removed"; });
        Step("Removing program files", () =>
        {
            if (!Directory.Exists(_o.InstallDir)) return "nothing to remove";
            for (var i = 0; i < 5; i++)
            {
                try { Directory.Delete(_o.InstallDir, true); return "removed"; }
                catch (IOException) { Thread.Sleep(1000); }
                catch (UnauthorizedAccessException) { Thread.Sleep(1000); }
            }
            return "some files are still in use and will be removed after a restart";
        }, fatal: false);
        Step(_o.Purge ? "Deleting local data (queue, identity, logs)" : "Keeping local data", () =>
        {
            if (!_o.Purge) return $"{AgentSetup.DefaultDataDir} kept (use --purge to delete)";
            try { Directory.Delete(AgentSetup.DefaultDataDir, true); return "deleted"; }
            catch (IOException ex) { return "partially deleted: " + ex.Message; }
        }, fatal: false);
        return new StepResult(true, "NISS Monitor agent uninstalled." + (_o.Purge ? "" : $"\nLocal data kept in {AgentSetup.DefaultDataDir}."));
    }

    private string SettingsPath => Path.Combine(_o.InstallDir, "agentsettings.json");

    private JsonNode? ReadSettings()
    {
        try { return File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath)) : null; }
        catch (JsonException) { return null; }
    }

    private static JsonObject Ensure(JsonNode parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string Cli(string exe, string args, bool tolerateFailure = false)
    {
        var r = AgentSetup.Run(exe, args, 120_000);
        if (r.Ok) return r.Output.Trim();
        if (tolerateFailure) return "ERR:" + r.Output;
        throw new InvalidOperationException($"NISSAgent {args.Split(' ')[0]} failed: {r.Output}");
    }

    /// <summary>Interactive install: if no code was supplied and this PC is not yet enrolled, ask the employee for it.</summary>
    private void PromptForEnrollmentCode(string cli)
    {
        if (_o.Silent || _o.SkipEnroll || !string.IsNullOrWhiteSpace(_o.Token) || Console.IsInputRedirected) return;
        var enrolled = Cli(cli, "--device-id", tolerateFailure: true);
        if (!enrolled.StartsWith("ERR:", StringComparison.Ordinal)) return; // already enrolled - no code needed

        Console.WriteLine();
        if (string.IsNullOrWhiteSpace(_o.Server) || _o.Server.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write(string.IsNullOrWhiteSpace(_o.Server) ? "Server URL (https://<server>:8443): " : $"Server URL [{_o.Server}]: ");
            var s = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(s)) _o.Server = s;
        }
        Console.Write("Enter the enrollment code from your administrator: ");
        _o.Token = Console.ReadLine()?.Trim();
        Console.WriteLine();
    }

    private void Step(string title, Func<string> action, bool fatal = true)
    {
        _n++;
        Console.Write($"[{_n,2}/{_total}] {title} ... ");
        try
        {
            var detail = action();
            Console.WriteLine("OK" + (string.IsNullOrEmpty(detail) ? "" : $"  ({detail})"));
            _log.Write($"[{_n}/{_total}] {title}: OK {detail}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine(fatal ? "FAILED" : "WARNING");
            Console.WriteLine("       " + ex.Message);
            _log.Write($"[{_n}/{_total}] {title}: {(fatal ? "FAILED" : "WARNING")} {ex.Message}");
            if (fatal) throw;
        }
    }
}

/// <summary>Finds the payload: embedded resource, NISSMonitorPayload.zip next to the exe, or a payload folder.</summary>
internal sealed class PayloadSource
{
    private readonly Func<Stream>? _zip;
    private readonly string? _folder;

    private PayloadSource(Func<Stream>? zip, string? folder) { _zip = zip; _folder = folder; }

    public static PayloadSource? Find()
    {
        var asm = Assembly.GetExecutingAssembly();
        if (asm.GetManifestResourceNames().Contains("NISSMonitorPayload.zip"))
            return new PayloadSource(() => asm.GetManifestResourceStream("NISSMonitorPayload.zip")!, null);

        var baseDir = AppContext.BaseDirectory;
        var zip = Path.Combine(baseDir, "NISSMonitorPayload.zip");
        if (File.Exists(zip)) return new PayloadSource(() => File.OpenRead(zip), null);
        var folder = Path.Combine(baseDir, "payload");
        return Directory.Exists(folder) && File.Exists(Path.Combine(folder, AgentSetup.ServiceExe)) ? new PayloadSource(null, folder) : null;
    }

    public void ExtractTo(string target)
    {
        if (_zip is not null)
        {
            using var s = _zip();
            using var za = new ZipArchive(s, ZipArchiveMode.Read);
            foreach (var entry in za.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!dest.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Payload contains an invalid path.");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
            return;
        }
        foreach (var file in Directory.EnumerateFiles(_folder!, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(_folder!, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}

internal sealed class InstallLog
{
    public string Path { get; }

    public InstallLog(string dataDir)
    {
        var dir = System.IO.Path.Combine(dataDir, "Logs");
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dir = System.IO.Path.GetTempPath(); }
        Path = System.IO.Path.Combine(dir, $"installer-{DateTime.Now:yyyyMMdd}.log");
    }

    public void Write(string message)
    {
        // Logging must never crash the installer - a permissions problem here is reported by the step itself, not here.
        try { File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class NativeMessageBox
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string text, string caption, bool ok) => MessageBoxW(IntPtr.Zero, text, caption, ok ? 0x40u : 0x10u);
}
