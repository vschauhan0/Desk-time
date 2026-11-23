using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Categories;
using NISS.Application.Options;
using NISS.Application.Settings;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using RuleEntity = NISS.Domain.Entities.AppCategoryRule;

namespace NISS.Application.Seeding;

/// <summary>What the seeder created that the operator needs to know about.</summary>
/// <param name="GeneratedAdminPassword">Set only when the bootstrap admin was created with a generated password (shown once, never stored in clear).</param>
public sealed record SeedResult(string? BootstrapAdminUsername, string? GeneratedAdminPassword);

/// <summary>Idempotent creation of default data: global policy, settings, category rules, bootstrap admin.</summary>
public interface IDataSeeder
{
    Task<SeedResult> SeedAsync(CancellationToken ct);
}

internal sealed class DataSeeder(
    IAppDbContext db, ISettingsService settings, IPasswordService passwords, TimeProvider clock,
    IOptions<MonitoringOptions> monitoring, IOptions<SecurityOptions> security, ILogger<DataSeeder> log) : IDataSeeder
{
    public async Task<SeedResult> SeedAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await SeedPolicyAsync(now, ct);
        await SeedSettingsAsync(ct);
        await SeedCategoriesAsync(ct);
        return await SeedAdminAsync(now, ct);
    }

    private async Task SeedPolicyAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await db.ConfigurationPolicies.AnyAsync(p => p.Scope == PolicyScope.Global, ct)) return;
        var m = monitoring.Value;
        var policy = new AgentPolicy
        {
            HeartbeatSeconds = m.HeartbeatSeconds, MetricsSeconds = m.MetricsSeconds, IdleThresholdMinutes = m.IdleThresholdMinutes,
            BacklogWarningThreshold = m.BacklogWarningThreshold, MinAgentVersion = string.IsNullOrWhiteSpace(m.MinAgentVersion) ? null : m.MinAgentVersion,
        };
        db.ConfigurationPolicies.Add(new ConfigurationPolicy
        {
            Id = Guid.NewGuid(), Name = "Default policy", Scope = PolicyScope.Global, ScopeValue = "",
            PolicyJson = System.Text.Json.JsonSerializer.Serialize(policy, ProtocolJson.Options), UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded the default global policy");
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        // Persist the defaults once so later edits (and config changes) are explicit, not accidental.
        if (await settings.GetRawAsync(SettingKeys.Retention, ct) is null) await settings.SetRetentionAsync(await settings.GetRetentionAsync(ct), ct);
        if (await settings.GetRawAsync(SettingKeys.HealthThresholds, ct) is null) await settings.SetThresholdsAsync(await settings.GetThresholdsAsync(ct), ct);
        if (await settings.GetRawAsync(SettingKeys.PolicyVersion, ct) is null) await settings.SetRawAsync(SettingKeys.PolicyVersion, "1", ct);
    }

    private async Task SeedCategoriesAsync(CancellationToken ct)
    {
        if (await db.AppCategoryRules.AnyAsync(ct)) return;
        foreach (var (pattern, category) in DefaultCategoryRules.Rules)
            db.AppCategoryRules.Add(new RuleEntity { Id = Guid.NewGuid(), Pattern = pattern, Category = category, Priority = category == AppCategories.System ? 50 : 100 });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded {Count} default application category rules", DefaultCategoryRules.Rules.Count);
    }

    private async Task<SeedResult> SeedAdminAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await db.AdminUsers.AnyAsync(ct)) return new SeedResult(null, null);
        var s = security.Value;
        var username = string.IsNullOrWhiteSpace(s.BootstrapAdminUsername) ? "admin" : s.BootstrapAdminUsername.Trim();
        var generated = string.IsNullOrEmpty(s.BootstrapAdminPassword);
        var password = generated ? GeneratePassword() : s.BootstrapAdminPassword;

        db.AdminUsers.Add(new AdminUser
        {
            Id = Guid.NewGuid(), Username = username, DisplayName = "Administrator", Role = Roles.SuperAdmin, Enabled = true,
            PasswordHash = passwords.Hash(password), CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        log.LogWarning("Created the bootstrap SUPERADMIN account {Username}{Generated}", username, generated ? " with a generated password (shown once on the console)" : "");
        return new SeedResult(username, generated ? password : null);
    }

    /// <summary>24 characters from an unambiguous alphabet, containing every character class.</summary>
    internal static string GeneratePassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", symbols = "!#$%&*+-=?@";
        var all = lower + upper + digits + symbols;
        var chars = new List<char>
        {
            lower[RandomNumberGenerator.GetInt32(lower.Length)], upper[RandomNumberGenerator.GetInt32(upper.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)], symbols[RandomNumberGenerator.GetInt32(symbols.Length)],
        };
        while (chars.Count < 24) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        RandomNumberGenerator.Shuffle(CollectionsMarshalSpan(chars));
        return new string(chars.ToArray());
    }

    private static Span<char> CollectionsMarshalSpan(List<char> list) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
}
