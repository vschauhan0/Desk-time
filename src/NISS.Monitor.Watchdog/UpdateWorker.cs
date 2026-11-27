using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Core.Updates;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Infrastructure.Runtime;
using NISS.Monitor.Infrastructure.Updates;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Watchdog;

/// <summary>
/// Runs agent updates from the SYSTEM watchdog so a wedged main service is still recoverable.
///
/// Target discovery: the main service records the heartbeat's assigned target in agent.db; the watchdog reads it. If that
/// hint is missing or stale (service wedged, crashed or not heartbeating) the watchdog falls back to its OWN
/// device-authenticated heartbeat - the independent channel. It is only used when the service has not heartbeated
/// recently, so it cannot overwrite fresher live data the service reported.
/// Mutual repair stays in <see cref="WatchdogWorker"/> (the watchdog restarts the service; the service's peer watchdog
/// restarts this one); a stopped service mid-update is simply started again by either path using the already-switched binPath.
/// </summary>
public sealed class UpdateWorker : BackgroundService
{
    private static readonly TimeSpan HintStale = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromHours(1);

    private readonly UpdateSettings _cfg;
    private readonly SqliteAgentStateStore _state;
    private readonly DeviceIdentityStore _identity;
    private readonly HttpClient _http;
    private readonly ILogger<UpdateWorker> _log;
    private readonly TimeProvider _time;
    private readonly string _installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new();

    public UpdateWorker(IOptions<AgentSettings> settings, SqliteAgentStateStore state, DeviceIdentityStore identity, ServerTrust trust,
        TimeProvider time, ILogger<UpdateWorker> log)
    {
        _cfg = settings.Value.Update; _state = state; _identity = identity; _time = time; _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    cert is not null && trust.Validate(cert as X509Certificate2 ?? new X509Certificate2(cert), errors),
            },
        }) { Timeout = TimeSpan.FromMinutes(10) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_cfg.AcceptedPublisherSubjects.Length == 0)
        {
            _log.LogInformation("Agent auto-update disabled: no Agent:Update:AcceptedPublisherSubjects configured");
            return;
        }
        try
        {
            var host = BuildHost();
            if (host is null) _log.LogWarning("Auto-update idle: device is not enrolled yet");
            else
            {
                var resolved = await host.ResumePendingAsync(UpdateOrchestrator.HealthWindow, stoppingToken).ConfigureAwait(false);
                if (resolved is not null) _log.LogInformation("Resolved a pending update switch; running version is {Version}", resolved);
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, _cfg.PollSeconds)), _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Update check failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var host = BuildHost();
        if (host is null) return;
        var current = host.CurrentVersion;

        var (target, url) = await DiscoverTargetAsync(current, ct).ConfigureAwait(false);
        if (target is null || url is null || !VersionCompare.IsNewer(target, current)) return;
        if (_failedAt.TryGetValue(target, out var t) && _time.GetUtcNow() - t < FailureCooldown) return;

        // Spread the fleet's package pulls: a random per-device wait before downloading (the host also honours 503 Retry-After).
        var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Max(0, _cfg.DownloadJitterMaxSeconds));
        _log.LogInformation("Update {Target} assigned (current {Current}); starting in {Seconds:0}s", target, current, jitter.TotalSeconds);
        await Task.Delay(jitter, _time, ct).ConfigureAwait(false);

        var orchestrator = new UpdateOrchestrator(host, new WinAuthenticodeChecker(), new PublisherPin(_cfg.AcceptedPublisherSubjects));
        await orchestrator.RunAsync(target, url, current, ct).ConfigureAwait(false);

        if (host.CurrentVersion != target) _failedAt[target] = _time.GetUtcNow();
        else _log.LogInformation("Agent updated to {Version}", target);
    }

    private async Task<(string? target, string? url)> DiscoverTargetAsync(string current, CancellationToken ct)
    {
        var okText = await _state.GetAsync(AgentStateKeys.HeartbeatOkUtc, ct).ConfigureAwait(false);
        if (DateTimeOffset.TryParse(okText ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            && _time.GetUtcNow() - at < HintStale)
        {
            var target = await _state.GetAsync(AgentStateKeys.UpdateTarget, ct).ConfigureAwait(false);
            var url = await _state.GetAsync(AgentStateKeys.UpdateUrl, ct).ConfigureAwait(false);
            return (string.IsNullOrEmpty(target) ? null : target, string.IsNullOrEmpty(url) ? null : url);
        }

        // Service has not heartbeated recently: poll the server over the watchdog's own device-authenticated channel.
        var id = _identity.Reload();
        if (id is null) return (null, null);
        var req = new HeartbeatRequest(id.DeviceId, current, _time.GetUtcNow(), 0, "WatchdogProbe", [], 0, null, null,
            "Main service not heartbeating; watchdog probe", 0, 0, null);
        using var msg = new HttpRequestMessage(HttpMethod.Post, BaseUrl(id) + "/api/v1/devices/heartbeat")
        {
            Content = JsonContent.Create(req, options: ProtocolJson.Options),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("NissDevice", $"{id.DeviceId}:{id.DeviceSecret}");
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (null, null);
        var hb = await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(ProtocolJson.Options, ct).ConfigureAwait(false);
        return hb?.TargetAgentVersion is { Length: > 0 } tv && hb.UpdateUrl is { Length: > 0 } u ? (tv, u) : (null, null);
    }

    private static string BaseUrl(DeviceIdentity id) => id.ServerBaseUrl.TrimEnd('/');

    private WindowsUpdateHost? BuildHost()
    {
        var id = _identity.Current ?? _identity.Reload();
        if (id is null) return null;
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NISS", "Agent");
        var legacy = ReadInstalledVersion(Path.Combine(_installDir, AgentConstants.ServiceExeName)) ?? "0.0.0";
        var opts = new UpdateHostOptions
        {
            InstallDir = _installDir,
            StatePath = Path.Combine(dataDir, "update-state.json"),
            LegacyVersion = legacy,
            ServerBaseUrl = new Uri(BaseUrl(id) + "/"),
            AllowInsecureHttp = _cfg.AllowInsecureHttp,
            Time = _time,
            Auth = () => _identity.Current is { } c ? new AuthenticationHeaderValue("NissDevice", $"{c.DeviceId}:{c.DeviceSecret}") : null,
            ReadHealth = ReadHealth,
        };
        return new WindowsUpdateHost(opts, _http, new WindowsUpdateServiceControl(m => _log.LogInformation("{Message}", m)), ReportAsync);
    }

    private (string?, DateTimeOffset?) ReadHealth()
    {
        var v = _state.GetAsync(AgentStateKeys.RunningVersion, CancellationToken.None).GetAwaiter().GetResult();
        var t = _state.GetAsync(AgentStateKeys.HeartbeatOkUtc, CancellationToken.None).GetAwaiter().GetResult();
        return (v, DateTimeOffset.TryParse(t ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null);
    }

    /// <summary>Best-effort status report; never throws (reporting must not break the update itself).</summary>
    private async Task ReportAsync(UpdateStatusReport report)
    {
        try
        {
            var id = _identity.Current;
            if (id is null) return;
            using var msg = new HttpRequestMessage(HttpMethod.Post, BaseUrl(id) + "/api/v1/agent/update/status")
            {
                Content = JsonContent.Create(report, options: ProtocolJson.Options),
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("NissDevice", $"{id.DeviceId}:{id.DeviceSecret}");
            using var resp = await _http.SendAsync(msg).ConfigureAwait(false);
            _log.LogInformation("Update {Version}: {Phase} ({Status})", report.Version, report.Phase, (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _log.LogDebug(ex, "Update status report failed"); }
    }

    private static string? ReadInstalledVersion(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            return v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { return null; }
    }
}
