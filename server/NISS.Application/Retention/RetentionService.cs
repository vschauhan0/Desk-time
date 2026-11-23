using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Settings;

namespace NISS.Application.Retention;

/// <summary>Prunes old data according to the retention settings (0 days = keep forever).</summary>
public interface IRetentionService
{
    /// <summary>Runs one pruning pass. Returns the number of rows removed per table group.</summary>
    Task<IReadOnlyDictionary<string, long>> RunAsync(CancellationToken ct);
}

internal sealed class RetentionService(IRetentionStore store, ISettingsService settings, TimeProvider clock, ILogger<RetentionService> log) : IRetentionService
{
    public async Task<IReadOnlyDictionary<string, long>> RunAsync(CancellationToken ct)
    {
        var r = await settings.GetRetentionAsync(ct);
        var now = clock.GetUtcNow();
        var result = new Dictionary<string, long>();

        async Task Prune(string group, int days, params RetentionTarget[] targets)
        {
            if (days <= 0) return;
            var cutoff = now.AddDays(-days);
            long total = 0;
            foreach (var t in targets) total += await store.DeleteOlderThanAsync(t, cutoff, ct);
            result[group] = total;
        }

        await Prune("rawEvents", r.RawEventsDays, RetentionTarget.EventIngestion, RetentionTarget.ProcessEvents, RetentionTarget.InputActivity,
            RetentionTarget.IdleSessions, RetentionTarget.FileEvents, RetentionTarget.UsbEvents);
        await Prune("applicationSummaries", r.ApplicationSummariesDays, RetentionTarget.ApplicationSessions, RetentionTarget.ProcessSessions, RetentionTarget.UserSessions);
        await Prune("deviceHealth", r.DeviceHealthDays, RetentionTarget.SystemMetrics, RetentionTarget.Heartbeats, RetentionTarget.SecurityEvents);
        await Prune("auditLogs", r.AuditLogsDays, RetentionTarget.AuditLogs);
        // Expired refresh tokens are housekeeping, not policy.
        result["refreshTokens"] = await store.DeleteOlderThanAsync(RetentionTarget.RefreshTokens, now.AddDays(-7), ct);

        log.LogInformation("Retention pass finished: {Result}", string.Join(", ", result.Select(kv => $"{kv.Key}={kv.Value}")));
        return result;
    }
}

/// <summary>Removes stale enrollment tokens (expired for over a week, or used over 30 days ago).</summary>
public interface IEnrollmentTokenCleanupService
{
    Task<int> RunAsync(CancellationToken ct);
}

internal sealed class EnrollmentTokenCleanupService(IAppDbContext db, TimeProvider clock, ILogger<EnrollmentTokenCleanupService> log) : IEnrollmentTokenCleanupService
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var unusedCutoff = now.AddDays(-7);
        var usedCutoff = now.AddDays(-30);
        var removed = await db.EnrollmentTokens
            .Where(t => (t.UsedAt == null && t.ExpiresAt < unusedCutoff) || (t.UsedAt != null && t.UsedAt < usedCutoff))
            .ExecuteDeleteAsync(ct);
        if (removed > 0) log.LogInformation("Removed {Count} stale enrollment token(s)", removed);
        return removed;
    }
}
