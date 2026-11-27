using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Options;
using NISS.Application.Screenshots;
using NISS.Application.Settings;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Retention;

/// <summary>Prunes old data according to the retention settings (0 days = keep forever).</summary>
public interface IRetentionService
{
    /// <summary>Runs one pruning pass. Returns the number of rows removed per table group.</summary>
    Task<IReadOnlyDictionary<string, long>> RunAsync(CancellationToken ct);
}

internal sealed class RetentionService(
    IRetentionStore store,
    ISettingsService settings,
    IAppDbContext db,
    IScreenshotStore screenshots,
    IOptions<ScreenshotStorageOptions> screenshotOptions,
    TimeProvider clock,
    ILogger<RetentionService> log) : IRetentionService
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
        // Screenshots are files on disk plus rows: delete both through the store (ExecuteDelete would orphan the files).
        // Periodic and flagged captures expire on separate schedules; flagged ones are evidence and normally kept longer.
        await PruneScreenshotsAsync("screenshots", r.ScreenshotDays ?? screenshotOptions.Value.RetentionDays,
            s => s.Trigger != ScreenTriggers.Flagged);
        await PruneScreenshotsAsync("flaggedScreenshots", r.FlaggedScreenshotDays ?? screenshotOptions.Value.FlaggedRetentionDays,
            s => s.Trigger == ScreenTriggers.Flagged);

        async Task PruneScreenshotsAsync(string group, int days, Expression<Func<Screenshot, bool>> selector)
        {
            if (days <= 0) return;
            var cutoff = now.AddDays(-days);
            var expired = await db.Screenshots.Where(selector).Where(s => s.CapturedAtUtc < cutoff).ToListAsync(ct);
            if (expired.Count > 0) await screenshots.DeleteAsync(expired, ct);
            result[group] = expired.Count;
        }

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
