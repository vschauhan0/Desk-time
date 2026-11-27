using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Options;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Settings;

/// <summary>Reads/writes <c>server_settings</c> values (retention, health thresholds, policy version, generated secrets) with a short cache.</summary>
public interface ISettingsService
{
    Task<RetentionSettings> GetRetentionAsync(CancellationToken ct);
    Task SetRetentionAsync(RetentionSettings value, CancellationToken ct);
    Task<HealthThresholds> GetThresholdsAsync(CancellationToken ct);
    Task SetThresholdsAsync(HealthThresholds value, CancellationToken ct);
    /// <summary>Monotonic counter that changes whenever a configuration policy changes.</summary>
    Task<int> GetPolicyVersionAsync(CancellationToken ct);
    Task<int> BumpPolicyVersionAsync(CancellationToken ct);
    /// <summary>The configured screenshot storage root (empty = fall back to <c>ScreenshotStorage:Root</c>/temp).</summary>
    Task<ScreenshotStorageSettings> GetScreenshotStorageAsync(CancellationToken ct);
    Task SetScreenshotStorageAsync(ScreenshotStorageSettings value, CancellationToken ct);
    Task<string?> GetRawAsync(string key, CancellationToken ct);
    Task SetRawAsync(string key, string value, CancellationToken ct);
}

/// <summary>Well-known keys in <c>server_settings</c>.</summary>
public static class SettingKeys
{
    public const string JwtSigningKey = "jwt_signing_key";
    public const string Retention = "retention";
    public const string HealthThresholds = "health_thresholds";
    public const string PolicyVersion = "policy_version";
    public const string ScreenshotStorage = "screenshot_storage";
}

internal sealed class SettingsService(
    IAppDbContext db, IMemoryCache cache, TimeProvider clock,
    IOptions<RetentionOptions> retentionDefaults, IOptions<MonitoringOptions> monitoringDefaults,
    IOptions<ScreenshotStorageOptions> screenshotDefaults) : ISettingsService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public async Task<RetentionSettings> GetRetentionAsync(CancellationToken ct)
    {
        var stored = await GetJsonAsync(SettingKeys.Retention, () =>
        {
            var r = retentionDefaults.Value;
            return new RetentionSettings(r.RawEventsDays, r.ApplicationSummariesDays, r.DeviceHealthDays, r.AuditLogsDays);
        }, ct);
        // Screenshot values were added after this settings row first shipped: a row written before then has them null,
        // so fall back to the serversettings.json defaults rather than reading null as "keep forever".
        var shots = screenshotDefaults.Value;
        return stored with
        {
            ScreenshotDays = stored.ScreenshotDays ?? shots.RetentionDays,
            FlaggedScreenshotDays = stored.FlaggedScreenshotDays ?? shots.FlaggedRetentionDays,
        };
    }

    public Task SetRetentionAsync(RetentionSettings value, CancellationToken ct) => SetJsonAsync(SettingKeys.Retention, value, ct);

    public async Task<HealthThresholds> GetThresholdsAsync(CancellationToken ct) =>
        await GetJsonAsync(SettingKeys.HealthThresholds, () =>
        {
            var m = monitoringDefaults.Value;
            return new HealthThresholds(m.HeartbeatWarningSeconds, m.OfflineSeconds, m.BacklogWarningThreshold,
                string.IsNullOrWhiteSpace(m.MinAgentVersion) ? null : m.MinAgentVersion);
        }, ct);

    public Task SetThresholdsAsync(HealthThresholds value, CancellationToken ct) => SetJsonAsync(SettingKeys.HealthThresholds, value, ct);

    public async Task<ScreenshotStorageSettings> GetScreenshotStorageAsync(CancellationToken ct) =>
        await GetJsonAsync(SettingKeys.ScreenshotStorage, () => new ScreenshotStorageSettings(screenshotDefaults.Value.Root ?? ""), ct);

    public Task SetScreenshotStorageAsync(ScreenshotStorageSettings value, CancellationToken ct) =>
        SetJsonAsync(SettingKeys.ScreenshotStorage, value with { Root = value.Root?.Trim() ?? "" }, ct);

    public async Task<int> GetPolicyVersionAsync(CancellationToken ct)
    {
        var raw = await GetRawAsync(SettingKeys.PolicyVersion, ct);
        return int.TryParse(raw, out var v) ? v : 1;
    }

    public async Task<int> BumpPolicyVersionAsync(CancellationToken ct)
    {
        var next = await GetPolicyVersionAsync(ct) + 1;
        await SetRawAsync(SettingKeys.PolicyVersion, next.ToString(), ct);
        return next;
    }

    public async Task<string?> GetRawAsync(string key, CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey(key), out string? cached)) return cached;
        var value = await db.ServerSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        cache.Set(CacheKey(key), value, Ttl);
        return value;
    }

    public async Task SetRawAsync(string key, string value, CancellationToken ct)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.ServerSettings.Add(new ServerSetting { Key = key, Value = value, UpdatedAt = clock.GetUtcNow() });
        else { row.Value = value; row.UpdatedAt = clock.GetUtcNow(); }
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey(key));
    }

    private static string CacheKey(string key) => "setting:" + key;

    private async Task<T> GetJsonAsync<T>(string key, Func<T> defaults, CancellationToken ct) where T : class
    {
        var raw = await GetRawAsync(key, ct);
        if (raw is not null)
        {
            try { var v = JsonSerializer.Deserialize<T>(raw, ProtocolJson.Options); if (v is not null) return v; }
            catch (JsonException) { /* fall back to defaults */ }
        }
        return defaults();
    }

    private Task SetJsonAsync<T>(string key, T value, CancellationToken ct) =>
        SetRawAsync(key, JsonSerializer.Serialize(value, ProtocolJson.Options), ct);
}
