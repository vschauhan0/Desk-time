using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Http;

/// <summary>Holds the pinned server certificate thumbprint used by the HTTPS handler.</summary>
public sealed class ServerTrust
{
    private volatile string? _thumbprint;

    public ServerTrust(IOptions<AgentSettings> settings) => _thumbprint = Normalize(settings.Value.Server.CertificateThumbprint);

    /// <summary>Currently pinned thumbprint (SHA-256 or SHA-1 hex), if any.</summary>
    public string? Thumbprint { get => _thumbprint; set => _thumbprint = Normalize(value); }

    private static string? Normalize(string? t)
        => string.IsNullOrWhiteSpace(t) ? null : new string(t.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// TLS validation: if a thumbprint is pinned the certificate must match it; otherwise normal Windows chain
    /// validation applies. This allows a self-signed LAN certificate without disabling validation.
    /// </summary>
    public bool Validate(X509Certificate2? cert, System.Net.Security.SslPolicyErrors errors)
    {
        if (cert is null) return false;
        var pin = _thumbprint;
        if (pin is null) return errors is System.Net.Security.SslPolicyErrors.None or System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        var sha256 = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
        var sha1 = cert.Thumbprint;
        return string.Equals(pin, sha256, StringComparison.OrdinalIgnoreCase) || string.Equals(pin, sha1, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Outcome of a server call. Network failures are values, not exceptions.</summary>
public sealed record ApiResult<T>(bool Success, HttpStatusCode? StatusCode, T? Value, string? Error, TimeSpan RoundTrip, DateTimeOffset SentUtc, DateTimeOffset ReceivedUtc)
{
    /// <summary>True when the server was reached (any HTTP status).</summary>
    public bool Reachable => StatusCode is not null;

    /// <summary>401/403: bad or revoked credentials.</summary>
    public bool IsAuthFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>4xx other than 401/403/408/429: retrying the same payload will not help.</summary>
    public bool IsPermanentClientError => StatusCode is { } s && (int)s is >= 400 and < 500 && (int)s is not (401 or 403 or 408 or 429);
}

/// <summary>Typed HTTPS client for the NISS API. Every call has a timeout and never throws for network problems.</summary>
public sealed class ServerApiClient
{
    private readonly HttpClient _http;
    private readonly AgentSettings _settings;
    private readonly DeviceIdentityStore _identity;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerApiClient> _log;

    public ServerApiClient(HttpClient http, IOptions<AgentSettings> settings, DeviceIdentityStore identity, TimeProvider time, ILogger<ServerApiClient> log)
    {
        _http = http;
        _settings = settings.Value;
        _identity = identity;
        _time = time;
        _log = log;
    }

    /// <summary>Base URL: agentsettings.json wins; falls back to the URL recorded at enrollment.</summary>
    public string BaseUrl
    {
        get
        {
            var configured = _settings.Server.BaseUrl;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            return (_identity.Current?.ServerBaseUrl ?? "https://localhost:8443").TrimEnd('/');
        }
    }

    public Task<ApiResult<EnrollResponse>> EnrollAsync(string baseUrl, EnrollRequest request, CancellationToken ct)
        => SendAsync<EnrollResponse>(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/v1/devices/enroll", request, authenticated: false, ct);

    public Task<ApiResult<HeartbeatResponse>> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
        => SendAsync<HeartbeatResponse>(HttpMethod.Post, Url("/api/v1/devices/heartbeat"), request, true, ct);

    public Task<ApiResult<EventBatchResponse>> PostEventsAsync(EventBatchRequest request, CancellationToken ct)
        => SendAsync<EventBatchResponse>(HttpMethod.Post, Url("/api/v1/events/batch"), request, true, ct);

    public Task<ApiResult<AgentConfigResponse>> GetConfigAsync(CancellationToken ct)
        => SendAsync<AgentConfigResponse>(HttpMethod.Get, Url("/api/v1/agent/config"), null, true, ct);

    public Task<ApiResult<object>> PostStatusAsync(AgentStatusRequest request, CancellationToken ct)
        => SendAsync<object>(HttpMethod.Post, Url("/api/v1/agent/status"), request, true, ct);

    public Task<ApiResult<RotateCredentialsResponse>> RotateAsync(CancellationToken ct)
        => SendAsync<RotateCredentialsResponse>(HttpMethod.Post, Url("/api/v1/devices/credentials/rotate"), null, true, ct);

    public Task<ApiResult<HealthResponse>> HealthAsync(CancellationToken ct)
        => SendAsync<HealthResponse>(HttpMethod.Get, Url("/api/v1/health"), null, false, ct);

    private string Url(string path) => BaseUrl + path;

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, bool authenticated, CancellationToken ct)
    {
        var sent = _time.GetUtcNow();
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: ProtocolJson.Options);
            if (authenticated)
            {
                var id = _identity.Current;
                if (id is null) return new ApiResult<T>(false, null, default, "Device is not enrolled.", sw.Elapsed, sent, _time.GetUtcNow());
                req.Headers.Authorization = new AuthenticationHeaderValue("NissDevice", $"{id.DeviceId}:{id.DeviceSecret}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _settings.Server.RequestTimeoutSeconds)));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var received = _time.GetUtcNow();
            if (!resp.IsSuccessStatusCode)
            {
                var text = await SafeReadAsync(resp, timeout.Token).ConfigureAwait(false);
                return new ApiResult<T>(false, resp.StatusCode, default, $"HTTP {(int)resp.StatusCode}: {Truncate(text)}", sw.Elapsed, sent, received);
            }
            if (typeof(T) == typeof(object) || resp.StatusCode == HttpStatusCode.NoContent)
                return new ApiResult<T>(true, resp.StatusCode, default, null, sw.Elapsed, sent, received);

            var value = await resp.Content.ReadFromJsonAsync<T>(ProtocolJson.Options, timeout.Token).ConfigureAwait(false);
            return new ApiResult<T>(true, resp.StatusCode, value, null, sw.Elapsed, sent, received);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ApiResult<T>(false, null, default, "Request timed out.", sw.Elapsed, sent, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            _log.LogDebug(ex, "Server call {Method} {Path} failed", method, new Uri(url).AbsolutePath);
            return new ApiResult<T>(false, null, default, ex is HttpRequestException hre ? (hre.InnerException?.Message ?? hre.Message) : ex.Message, sw.Elapsed, sent, _time.GetUtcNow());
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return ""; }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
