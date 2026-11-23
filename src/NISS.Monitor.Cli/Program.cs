using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Infrastructure;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Setup;

namespace NISS.Monitor.Cli;

/// <summary>
/// NISSAgent.exe - administrative command line for the NISS endpoint agent. Every command except --version and --help
/// requires an elevated (administrator) prompt so ordinary employee accounts cannot reconfigure or read the agent.
/// Exit codes: 0 ok, 1 failure, 2 server unreachable, 3 credentials rejected, 4 not enrolled, 5 not elevated, 64 usage.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var flags = Parse(args);
        if (flags.ContainsKey("version")) { Console.WriteLine(AgentVersion.Current); return 0; }
        if (args.Length == 0 || flags.ContainsKey("help") || flags.ContainsKey("?")) { PrintUsage(); return args.Length == 0 ? 64 : 0; }

        if (!IsElevated() && Environment.GetEnvironmentVariable("NISS_AGENT_DATA") is null)
        {
            Console.Error.WriteLine("NISSAgent requires an elevated (administrator) command prompt.");
            return 5;
        }

        var baseDir = AppContext.BaseDirectory;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("agentsettings.json", optional: true)
            .AddJsonFile("agentsettings.local.json", optional: true)
            .AddEnvironmentVariables("NISS_")
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNissAgentInfrastructure(configuration);
        await using var sp = services.BuildServiceProvider();

        try
        {
            var commands = new Commands(sp, baseDir);
            if (flags.ContainsKey("device-id")) return commands.DeviceId();
            if (flags.ContainsKey("status")) return await commands.StatusAsync().ConfigureAwait(false);
            if (flags.ContainsKey("health")) return await commands.HealthAsync().ConfigureAwait(false);
            if (flags.ContainsKey("diagnostics")) return commands.Diagnostics(flags.GetValueOrDefault("diagnostics"));
            if (flags.ContainsKey("enroll")) return await commands.EnrollAsync(flags).ConfigureAwait(false);
            if (flags.ContainsKey("repair")) return commands.Repair();
            if (flags.ContainsKey("init")) return commands.Init();
            if (flags.ContainsKey("rotate-credentials")) return await commands.RotateAsync().ConfigureAwait(false);
            Console.Error.WriteLine("Unknown command.");
            PrintUsage();
            return 64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal) && !a.StartsWith('/')) continue;
            var key = a.TrimStart('-', '/');
            var eq = key.IndexOf('=');
            if (eq > 0) { map[key[..eq]] = key[(eq + 1)..]; continue; }
            var next = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
            map[key] = next;
            if (next is not null) i++;
        }
        return map;
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PrintUsage() => Console.WriteLine("""
        NISSAgent - NISS endpoint agent administration (run from an elevated prompt)

          --status                 service state, enrollment, queue depth, last heartbeat/upload
          --device-id              print this device's DeviceId
          --health                 check server reachability and that the device credential is accepted
          --diagnostics [path]     write a redacted diagnostics bundle (JSON)
          --enroll --server URL --token TOKEN [--employee EMP1024] [--thumbprint HEX]
                                   enroll this device with a one-time token created in the dashboard
          --rotate-credentials     replace the per-device secret
          --repair                 re-apply service registration, recovery, ACLs, Run key and restart the service
          --init                   create/upgrade the local SQLite database
          --version                print the agent version
        """);
}

/// <summary>Implementation of the CLI commands.</summary>
internal sealed class Commands
{
    private readonly IServiceProvider _sp;
    private readonly string _baseDir;

    public Commands(IServiceProvider sp, string baseDir)
    {
        _sp = sp;
        _baseDir = baseDir;
    }

    private T Get<T>() where T : notnull => _sp.GetRequiredService<T>();

    public int Init()
    {
        Get<SqliteDatabase>().EnsureInitialized();
        Console.WriteLine($"Database ready: {Get<SqliteDatabase>().Path}");
        return 0;
    }

    public int DeviceId()
    {
        Get<SqliteDatabase>().EnsureInitialized();
        var id = Get<DeviceIdentityStore>().Reload();
        if (id is null) { Console.Error.WriteLine("Not enrolled."); return 4; }
        Console.WriteLine(id.DeviceId);
        return 0;
    }

    public async Task<int> StatusAsync()
    {
        Get<SqliteDatabase>().EnsureInitialized();
        var id = Get<DeviceIdentityStore>().Reload();
        var stats = await Get<IEventQueue>().GetStatsAsync(default).ConfigureAwait(false);
        var settings = Get<IOptions<AgentSettings>>().Value;
        var heartbeats = Get<LocalTelemetryStore>().RecentHeartbeats(1);
        var sessionAgents = Process.GetProcessesByName("NISSMonitor.Session").Length;

        Console.WriteLine($"Agent version    : {AgentVersion.Current}");
        Console.WriteLine($"Service          : {AgentSetup.ServiceState()}");
        Console.WriteLine($"Watchdog         : {AgentSetup.ServiceState(AgentSetup.WatchdogServiceName)}");
        Console.WriteLine($"Session agents   : {sessionAgents} running");
        Console.WriteLine($"Enrolled         : {(id is null ? "NO" : "yes")}");
        if (id is not null)
        {
            Console.WriteLine($"Device id        : {id.DeviceId}");
            Console.WriteLine($"Employee         : {id.EmployeeId ?? "(unassigned)"}");
            Console.WriteLine($"Enrolled at      : {id.EnrolledAtUtc:u}");
        }
        Console.WriteLine($"Server           : {settings.Server.BaseUrl}");
        Console.WriteLine($"Queue            : {stats.Pending} pending, {stats.DeadLetters} dead-lettered (oldest {(stats.OldestPendingUtc is null ? "-" : stats.OldestPendingUtc.Value.ToString("u"))})");
        Console.WriteLine($"Last upload      : {(stats.LastUploadUtc is null ? "never" : stats.LastUploadUtc.Value.ToString("u"))}");
        Console.WriteLine($"Last heartbeat   : {(heartbeats.Count == 0 ? "never" : $"{heartbeats[0].SentUtc:u} ({(heartbeats[0].Success ? "ok" : "FAILED: " + heartbeats[0].Error)})")}");
        if (!string.IsNullOrEmpty(stats.LastError)) Console.WriteLine($"Last error       : {stats.LastError}");
        return id is null ? 4 : 0;
    }

    public async Task<int> HealthAsync()
    {
        Get<SqliteDatabase>().EnsureInitialized();
        var api = Get<ServerApiClient>();
        var health = await api.HealthAsync(default).ConfigureAwait(false);
        if (!health.Success || health.Value is null)
        {
            Console.WriteLine($"Server {api.BaseUrl}: UNREACHABLE ({health.Error})");
            return 2;
        }
        Console.WriteLine($"Server {api.BaseUrl}: {health.Value.Status}, version {health.Value.Version}, database {(health.Value.Database ? "ok" : "DOWN")}, round-trip {health.RoundTrip.TotalMilliseconds:0} ms");

        if (Get<DeviceIdentityStore>().Reload() is null) { Console.WriteLine("Device is not enrolled."); return 4; }
        var cfg = await api.GetConfigAsync(default).ConfigureAwait(false);
        if (cfg.IsAuthFailure) { Console.WriteLine($"Device credential REJECTED by the server ({cfg.StatusCode})."); return 3; }
        if (!cfg.Success) { Console.WriteLine($"Authenticated call failed: {cfg.Error}"); return 1; }
        Console.WriteLine($"Device credential accepted (device {cfg.Value!.DeviceId}, policy v{cfg.Value.Policy.Version}).");
        return 0;
    }

    public async Task<int> EnrollAsync(Dictionary<string, string?> flags)
    {
        var token = flags.GetValueOrDefault("token");
        if (string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("--token is required."); return 64; }
        var settings = Get<IOptions<AgentSettings>>().Value;
        var server = flags.GetValueOrDefault("server") ?? settings.Server.BaseUrl;
        var thumb = flags.GetValueOrDefault("thumbprint") ?? settings.Server.CertificateThumbprint;

        Get<SqliteDatabase>().EnsureInitialized();
        var identity = await Get<IDeviceEnrollmentService>().EnrollAsync(
            new EnrollmentRequestOptions(server, token, flags.GetValueOrDefault("employee"), string.IsNullOrWhiteSpace(thumb) ? null : thumb), default).ConfigureAwait(false);

        PersistServerSettings(server, thumb);
        Console.WriteLine($"Enrolled as {identity.DeviceId}" + (identity.EmployeeId is null ? "" : $" (employee {identity.EmployeeId})"));
        return 0;
    }

    public async Task<int> RotateAsync()
    {
        Get<SqliteDatabase>().EnsureInitialized();
        await Get<IDeviceEnrollmentService>().RotateCredentialsAsync(default).ConfigureAwait(false);
        Console.WriteLine("Device credential rotated.");
        return 0;
    }

    public int Repair()
    {
        Get<SqliteDatabase>().EnsureInitialized();
        var paths = Get<AgentPaths>();
        Step("Local database", () => Get<SqliteDatabase>().EnsureInitialized());
        Step("Data directory permissions", () => AgentSetup.SecureDataDirectory(paths.DataDirectory));
        if (File.Exists(Path.Combine(_baseDir, AgentSetup.ServiceExe)))
        {
            Step("Service registration + recovery", () => AgentSetup.InstallOrUpdateService(_baseDir, _ => { }));
            if (File.Exists(Path.Combine(_baseDir, AgentSetup.WatchdogExe)))
                Step("Watchdog registration + recovery", () => AgentSetup.InstallOrUpdateWatchdogService(_baseDir, _ => { }));
            Step("Session agent startup entry", () => AgentSetup.SetSessionRunKey(_baseDir));
            Step("Restart services", () =>
            {
                AgentSetup.StopService(TimeSpan.FromSeconds(15), AgentSetup.WatchdogServiceName);
                AgentSetup.StopService(TimeSpan.FromSeconds(30));
                AgentSetup.StartService(TimeSpan.FromSeconds(30));
                if (File.Exists(Path.Combine(_baseDir, AgentSetup.WatchdogExe))) AgentSetup.StartService(TimeSpan.FromSeconds(20), AgentSetup.WatchdogServiceName);
            });
        }
        return 0;
    }

    public int Diagnostics(string? outPath)
    {
        Get<SqliteDatabase>().EnsureInitialized();
        var paths = Get<AgentPaths>();
        var settings = Get<IOptions<AgentSettings>>().Value;
        var id = Get<DeviceIdentityStore>().Reload();
        var stats = Get<IEventQueue>().GetStatsAsync(default).GetAwaiter().GetResult();
        var machine = Get<MachineInfo>();

        var bundle = new JsonObject
        {
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
            ["agentVersion"] = AgentVersion.Current,
            ["service"] = AgentSetup.ServiceState(),
            ["watchdog"] = AgentSetup.ServiceState(AgentSetup.WatchdogServiceName),
            ["sessionAgentProcesses"] = Process.GetProcessesByName("NISSMonitor.Session").Length,
            ["device"] = new JsonObject { ["deviceId"] = id?.DeviceId, ["employeeId"] = id?.EmployeeId, ["enrolledUtc"] = id?.EnrolledAtUtc.ToString("o") },
            ["machine"] = new JsonObject { ["hostname"] = machine.Hostname, ["os"] = machine.OsVersion, ["manufacturer"] = machine.Manufacturer, ["model"] = machine.Model },
            ["server"] = new JsonObject { ["baseUrl"] = settings.Server.BaseUrl, ["certificatePinned"] = !string.IsNullOrWhiteSpace(settings.Server.CertificateThumbprint) },
            ["queue"] = new JsonObject
            {
                ["pending"] = stats.Pending, ["deadLetters"] = stats.DeadLetters, ["oldestPendingUtc"] = stats.OldestPendingUtc?.ToString("o"),
                ["lastUploadUtc"] = stats.LastUploadUtc?.ToString("o"), ["lastError"] = stats.LastError,
            },
            ["recentHeartbeats"] = new JsonArray(Get<LocalTelemetryStore>().RecentHeartbeats(10).Select(h => (JsonNode)new JsonObject
            {
                ["sentUtc"] = h.SentUtc.ToString("o"), ["success"] = h.Success, ["error"] = h.Error, ["rttMs"] = h.RttMs,
            }).ToArray()),
            ["logTail"] = new JsonArray(LogTail(paths.LogsDirectory, 150).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        };

        var target = string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(paths.DiagnosticsDirectory, $"diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
            : outPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllText(target, bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Diagnostics written to {target}");
        return 0;
    }

    private static IEnumerable<string> LogTail(string dir, int lines)
    {
        if (!Directory.Exists(dir)) return [];
        var newest = new DirectoryInfo(dir).GetFiles("agent-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return [];
        try
        {
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var all = new Queue<string>();
            while (sr.ReadLine() is { } line)
            {
                all.Enqueue(Redact(line));
                if (all.Count > lines) all.Dequeue();
            }
            return all;
        }
        catch (IOException) { return []; }
    }

    private static string Redact(string line)
        => System.Text.RegularExpressions.Regex.Replace(line, @"(NissDevice|Bearer)\s+\S+|(secret|token|password)=\S+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void PersistServerSettings(string server, string? thumbprint)
    {
        var path = Path.Combine(_baseDir, "agentsettings.json");
        JsonNode root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject() : new JsonObject();
        var agent = root["Agent"] as JsonObject ?? new JsonObject();
        root["Agent"] = agent;
        var srv = agent["Server"] as JsonObject ?? new JsonObject();
        agent["Server"] = srv;
        srv["BaseUrl"] = server;
        if (!string.IsNullOrWhiteSpace(thumbprint)) srv["CertificateThumbprint"] = thumbprint;
        try { File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (UnauthorizedAccessException) { Console.WriteLine("Note: could not update agentsettings.json (no write access); the server URL is stored with the device identity."); }
    }

    private static void Step(string name, Action action)
    {
        try { action(); Console.WriteLine($"[ ok ] {name}"); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }
}
