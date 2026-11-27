using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Updates;
using NISS.Monitor.Protocol;
using NISS.Monitor.Setup;

namespace NISS.Monitor.Infrastructure.Updates;

/// <summary>The SCM/process operations the update host needs. The real one never weakens the hardened service ACL.</summary>
public interface IUpdateServiceControl
{
    /// <summary>Repoint the main service at <paramref name="mainExePath"/> and re-apply the hardened DACL.</summary>
    void SwitchBinPath(string mainExePath);

    /// <summary>Stop the main service, end old-version session agents, start the service again.</summary>
    void RestartMainService();

    bool IsMainRunning();

    /// <summary>Point the interactive-logon Run key at the session exe in <paramref name="versionDir"/>.</summary>
    void SetSessionRunKey(string versionDir);
}

/// <summary>Production implementation (runs as SYSTEM inside the watchdog, which already holds stop/config rights).
/// The optional delegates are test seams: process runner, service-exists probe and the restart step.</summary>
public sealed class WindowsUpdateServiceControl(Action<string>? log = null, Func<string, string, ToolResult>? run = null,
    Func<string, bool>? serviceExists = null, Action? restart = null, Action<string>? setRunKey = null) : IUpdateServiceControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public void SwitchBinPath(string mainExePath) =>
        AgentSetup.SwitchServiceBinPath(AgentSetup.ServiceName, mainExePath, log, run, serviceExists);

    public void RestartMainService()
    {
        if (restart is not null) { restart(); return; }
        // SYSTEM holds SERVICE_STOP under the hardened DACL, so we stop it directly - AgentSetup.StopService would
        // UNHARDEN first, which an update must never do.
        using (var sc = new ServiceController(AgentSetup.ServiceName))
        {
            if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
        }
        // Old-version session agents would otherwise keep running from the previous folder (the new service's launcher
        // only starts a session when none is running); killing them lets the new service relaunch Session.exe from the new folder.
        AgentSetup.StopSessionAgents();
        AgentSetup.StartService(Timeout);
    }

    public bool IsMainRunning() => AgentSetup.ServiceState() == nameof(ServiceControllerStatus.Running);

    public void SetSessionRunKey(string versionDir) { if (setRunKey is not null) setRunKey(versionDir); else AgentSetup.SetSessionRunKey(versionDir); }
}

/// <summary>Everything the host needs from its environment.</summary>
public sealed class UpdateHostOptions
{
    public required string InstallDir { get; init; }
    public required string StatePath { get; init; }
    /// <summary>Version of the install currently in <see cref="InstallDir"/> itself (the pre-update layout).</summary>
    public required string LegacyVersion { get; init; }
    public required Uri ServerBaseUrl { get; init; }
    /// <summary>Authorization header value for device-authenticated calls ("NissDevice id:secret").</summary>
    public required Func<AuthenticationHeaderValue?> Auth { get; init; }
    /// <summary>Latest (running version, last successful heartbeat) the main service has recorded.</summary>
    public required Func<(string? Version, DateTimeOffset? HeartbeatOkUtc)> ReadHealth { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public long MaxPackageBytes { get; init; } = 200L * 1024 * 1024;
    public int MaxDownloadAttempts { get; init; } = 6;
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan HealthPoll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan StartupGrace { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Startup grace on the resume/reboot path: a delayed-start service after a reboot must not be rolled back spuriously.</summary>
    public TimeSpan ResumeStartupGrace { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Called with non-fatal operator-relevant notices (e.g. unsupported WatchdogChanged).</summary>
    public Action<string>? Warn { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Windows implementation of <see cref="IUpdateHost"/>: downloads over TLS, unzips zip-slip-safely, installs only the
/// manifest-listed files side-by-side under <c>app\&lt;version&gt;\</c>, repoints the service with <c>sc config binPath=</c>
/// (re-applying the hardened DACL), restarts it and health-gates on the new version's heartbeat.
/// The real swap needs a live service and cannot run in unit tests; see the manual checklist in
/// <c>docs/agent-updates-operations.md</c>. Cancellation during the health wait: the switch is already persisted as
/// <see cref="UpdateState.Pending"/>, so the cancel does NOT roll back; <see cref="ResumePendingAsync"/> re-runs the gate
/// on the next watchdog start (stopped service, reboot) and rolls back if it fails.
/// </summary>
public sealed class WindowsUpdateHost : IUpdateHost
{
    private const string ManifestName = "manifest.json";
    private static readonly string[] SiteSettingsFiles = ["agentsettings.json", "agentsettings.local.json"];
    private readonly UpdateHostOptions _o;
    private readonly HttpClient _http;
    private readonly IUpdateServiceControl _svc;
    private readonly Func<UpdateStatusReport, Task> _report;
    private readonly UpdateStateStore _store;

    public WindowsUpdateHost(UpdateHostOptions options, HttpClient http, IUpdateServiceControl svc, Func<UpdateStatusReport, Task> report)
    {
        _o = options; _http = http; _svc = svc; _report = report;
        _store = new UpdateStateStore(options.StatePath);
    }

    public UpdateState State => _store.Load();

    /// <summary>Version the service is running from (persisted state, else the legacy install).</summary>
    public string CurrentVersion => State.Current ?? _o.LegacyVersion;

    /// <summary>Folder containing the running version's executables.</summary>
    public string CurrentDir => UpdateLayout.ResolveVersionDir(_o.InstallDir, CurrentVersion, _o.LegacyVersion, Directory.Exists) ?? _o.InstallDir;

    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        var uri = Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs : new Uri(_o.ServerBaseUrl, url);
        if (uri.Scheme != Uri.UriSchemeHttps && !_o.AllowInsecureHttp)
            throw new InvalidOperationException("Update packages are only downloaded over TLS (https).");

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri) { Headers = { Authorization = _o.Auth() } };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < _o.MaxDownloadAttempts)
            {
                // The server's download gate sheds load with 503 + Retry-After: wait as told so the fleet spreads out.
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                if (wait > _o.MaxRetryAfter) wait = _o.MaxRetryAfter;
                await Task.Delay(wait, _o.Time, ct).ConfigureAwait(false);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is > 0 and var len && len > _o.MaxPackageBytes)
                throw new InvalidOperationException("Update package is larger than the allowed maximum.");
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > _o.MaxPackageBytes) throw new InvalidOperationException("Update package exceeds the allowed maximum size.");
            }
            return ms.ToArray();
        }
    }

    public UpdatePackage OpenPackage(byte[] package)
    {
        // Any InvalidDataException (short / partial / corrupt zip) propagates; the orchestrator reports it as failed.
        var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var manifestBytes = ReadEntry(archive, ManifestName) ?? throw new InvalidDataException("The package has no manifest.json.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, ProtocolJson.Options)
                       ?? throw new InvalidDataException("manifest.json is not a valid release manifest.");
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("The manifest lists no files.");
        foreach (var f in manifest.Files)
            if (UpdateLayout.SafeCombine("C:\\root", f.Path) is null)
                throw new InvalidDataException($"Unsafe path in manifest: {f.Path}");
        return new UpdatePackage(manifest, path => ReadEntry(archive, path) ?? throw new FileNotFoundException($"{path} is not in the package."));
    }

    public Task InstallSideBySideAsync(string version, byte[] package, CancellationToken ct)
    {
        var target = UpdateLayout.VersionDir(_o.InstallDir, version);
        // Re-open the SAME verified bytes; install only manifest-listed files, re-checking each hash as it is written.
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var manifest = ReadManifest(archive);
        if (manifest.WatchdogChanged)
            _o.Warn?.Invoke($"Release {version} changes the watchdog, but watchdog self-update is not supported in this agent version; the running watchdog binary is NOT replaced. Reinstall the watchdog manually.");
        InstallFiles(archive, manifest, version, target, ct, CurrentDir);
        return Task.CompletedTask;
    }

    internal static ReleaseManifest ReadManifest(ZipArchive archive)
    {
        var bytes = ReadEntry(archive, ManifestName) ?? throw new InvalidDataException("The package has no manifest.json.");
        return JsonSerializer.Deserialize<ReleaseManifest>(bytes, ProtocolJson.Options) ?? throw new InvalidDataException("Invalid manifest.");
    }

    /// <summary>Extracts exactly the manifest-listed files into a staging folder, then moves it into place.</summary>
    internal static void InstallFiles(ZipArchive archive, ReleaseManifest manifest, string version, string targetDir, CancellationToken ct, string? settingsSourceDir = null)
    {
        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
            throw new InvalidOperationException($"Package version {manifest.Version} does not match {version}.");
        var staging = targetDir + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var f in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = UpdateLayout.SafeCombine(staging, f.Path) ?? throw new InvalidDataException($"Unsafe path in manifest: {f.Path}");
                var bytes = ReadEntry(archive, f.Path) ?? throw new FileNotFoundException($"{f.Path} is not in the package.");
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 mismatch for {f.Path} at install time.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes);
            }
            // Per-site config is deliberately NOT in the package: carry the device's own files forward from where the running
            // service reads them, or the new version would fall back to code defaults (wrong server, no TLS pin) and fail its health gate.
            if (settingsSourceDir is not null)
                foreach (var name in SiteSettingsFiles)
                {
                    var src = Path.Combine(settingsSourceDir, name);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(staging, name), overwrite: true);
                }
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(staging, targetDir);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            throw;
        }
    }

    public Task SwitchToAsync(string version)
    {
        var dir = UpdateLayout.VersionDir(_o.InstallDir, version);
        var exe = Path.Combine(dir, AgentConstants.ServiceExeName);
        var st = State;
        // Persist FIRST: if we die between sc config and the health result, the next start resumes/rolls back.
        _store.Save(new UpdateState(version, st.Current ?? _o.LegacyVersion, version));
        _svc.SwitchBinPath(exe);
        _svc.SetSessionRunKey(dir);
        _svc.RestartMainService();
        return Task.CompletedTask;
    }

    public Task<bool> HealthyAsync(string version, TimeSpan window, CancellationToken ct) => HealthyAsync(version, window, _o.StartupGrace, ct);

    private async Task<bool> HealthyAsync(string version, TimeSpan window, TimeSpan startupGrace, CancellationToken ct)
    {
        var started = _o.Time.GetUtcNow();
        var deadline = started + window;
        var everRunning = false;
        while (_o.Time.GetUtcNow() < deadline)
        {
            var running = _svc.IsMainRunning();
            if (running) everRunning = true;
            else if (everRunning || _o.Time.GetUtcNow() - started > startupGrace) return false;   // crashed / never came up

            var (v, hb) = _o.ReadHealth();
            if (running && string.Equals(v, version, StringComparison.Ordinal) && hb is { } t && t >= started)
            {
                CompleteSwitch();
                return true;
            }
            // Cancellation here propagates WITHOUT rollback: Pending stays set and ResumePendingAsync re-gates on restart.
            await Task.Delay(_o.HealthPoll, _o.Time, ct).ConfigureAwait(false);
        }
        return false;
    }

    private void CompleteSwitch()
    {
        var st = State;
        _store.Save(st with { Pending = null });
        Prune(st.Current ?? CurrentVersion, st.Previous);
    }

    public Task RollbackAsync(string previousVersion)
    {
        var dir = UpdateLayout.ResolveVersionDir(_o.InstallDir, previousVersion, _o.LegacyVersion, Directory.Exists)
                  ?? throw new DirectoryNotFoundException($"Retained version {previousVersion} is missing; cannot roll back.");
        _svc.SwitchBinPath(Path.Combine(dir, AgentConstants.ServiceExeName));   // re-hardens the ACL
        _svc.SetSessionRunKey(dir);
        _svc.RestartMainService();
        _store.Save(new UpdateState(previousVersion, null, null));
        Prune(previousVersion, null);   // the failed version folder is no longer current/previous: drop it
        return Task.CompletedTask;
    }

    /// <summary>Keeps the current and previous version folders; removes the rest. Never throws.</summary>
    public void Prune(string current, string? previous)
    {
        var root = UpdateLayout.AppRoot(_o.InstallDir);
        if (!Directory.Exists(root)) return;
        var onDisk = Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
        foreach (var v in UpdateLayout.VersionsToPrune(onDisk, current, previous))
        {
            try { Directory.Delete(Path.Combine(root, v), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Startup recovery: if a switch was made but never health-confirmed (watchdog stopped / machine rebooted mid-gate),
    /// re-run the gate now; roll back to the retained previous version if it fails. Returns the version resolved, or null.
    /// </summary>
    public async Task<string?> ResumePendingAsync(TimeSpan window, CancellationToken ct, TimeSpan? startupGrace = null)
    {
        var st = State;
        if (st.Pending is not { } pending) return null;
        if (await HealthyAsync(pending, window, startupGrace ?? _o.ResumeStartupGrace, ct).ConfigureAwait(false))
        {
            await _report(new UpdateStatusReport(pending, "updated", null, 1)).ConfigureAwait(false);
            return pending;
        }
        var prev = st.Previous ?? _o.LegacyVersion;
        await RollbackAsync(prev).ConfigureAwait(false);
        await _report(new UpdateStatusReport(pending, "rolled_back", "Switch was not health-confirmed after a restart.", 1)).ConfigureAwait(false);
        return prev;
    }

    public Task ReportAsync(UpdateStatusReport report) => _report(report);

    private static byte[]? ReadEntry(ZipArchive archive, string path)
    {
        var norm = path.Replace('\\', '/');
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, norm, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
