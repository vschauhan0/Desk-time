using Microsoft.EntityFrameworkCore;
using Npgsql;
using NISS.Application.Abstractions;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence;

/// <summary>PostgreSQL specific ingestion SQL. Everything is parameterised.</summary>
internal sealed class IngestionStore(AppDbContext db) : IIngestionStore
{
    public async Task LockDeviceAsync(string deviceCode, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({deviceCode}, 0))", ct);

    public async Task<bool> TryInsertAsync(EventIngestion r, CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO event_ingestion
                (event_id, device_id, employee_id, session_id, event_type, "timestamp", received_at, sequence, source,
                 schema_version, clock_offset_ms, local_offset_minutes, subject, payload)
            VALUES
                ({r.EventId}, {r.DeviceId}, {r.EmployeeId}, {r.SessionId}, {r.EventType}, {r.Timestamp.ToUniversalTime()}, {r.ReceivedAt.ToUniversalTime()},
                 {r.Sequence}, {r.Source}, {r.SchemaVersion}, {r.ClockOffsetMs}, {r.LocalOffsetMinutes}, {r.Subject}, {r.Payload}::jsonb)
            ON CONFLICT (event_id) DO NOTHING
            """, ct);
        return rows == 1;
    }

    public async Task UpsertInputActivityAsync(InputActivity r, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO input_activity
                (id, device_id, employee_id, "timestamp", bucket_seconds, keyboard_activity, mouse_activity, click_count, double_click_count,
                 wheel_count, last_keyboard_at, last_mouse_at, optional_cursor_x, optional_cursor_y)
            VALUES
                ({r.Id}, {r.DeviceId}, {r.EmployeeId}, {r.Timestamp.ToUniversalTime()}, {r.BucketSeconds}, {r.KeyboardActivity}, {r.MouseActivity},
                 {r.ClickCount}, {r.DoubleClickCount}, {r.WheelCount}, {r.LastKeyboardAt?.ToUniversalTime()}, {r.LastMouseAt?.ToUniversalTime()},
                 {r.OptionalCursorX}, {r.OptionalCursorY})
            ON CONFLICT (device_id, "timestamp") DO UPDATE SET
                employee_id = COALESCE(input_activity.employee_id, EXCLUDED.employee_id),
                bucket_seconds = GREATEST(input_activity.bucket_seconds, EXCLUDED.bucket_seconds),
                keyboard_activity = GREATEST(input_activity.keyboard_activity, EXCLUDED.keyboard_activity),
                mouse_activity = GREATEST(input_activity.mouse_activity, EXCLUDED.mouse_activity),
                click_count = GREATEST(input_activity.click_count, EXCLUDED.click_count),
                double_click_count = GREATEST(input_activity.double_click_count, EXCLUDED.double_click_count),
                wheel_count = GREATEST(input_activity.wheel_count, EXCLUDED.wheel_count),
                last_keyboard_at = GREATEST(input_activity.last_keyboard_at, EXCLUDED.last_keyboard_at),
                last_mouse_at = GREATEST(input_activity.last_mouse_at, EXCLUDED.last_mouse_at),
                optional_cursor_x = COALESCE(EXCLUDED.optional_cursor_x, input_activity.optional_cursor_x),
                optional_cursor_y = COALESCE(EXCLUDED.optional_cursor_y, input_activity.optional_cursor_y)
            """, ct);
}

internal sealed class DeviceNumberSequence(AppDbContext db) : IDeviceNumberSequence
{
    public Task<long> NextAsync(CancellationToken ct) =>
        db.Database.SqlQuery<long>($"SELECT nextval('device_number_seq') AS \"Value\"").SingleAsync(ct);
}

/// <summary>Batched deletes for the retention job. Table and column names come from a fixed map, the cutoff is a parameter.</summary>
internal sealed class RetentionStore(AppDbContext db) : IRetentionStore
{
    private const int BatchSize = 5000;

    private static readonly Dictionary<RetentionTarget, (string Table, string Condition)> Map = new()
    {
        [RetentionTarget.EventIngestion] = ("event_ingestion", "\"timestamp\" < @cutoff"),
        [RetentionTarget.ProcessEvents] = ("process_events", "\"timestamp\" < @cutoff"),
        [RetentionTarget.InputActivity] = ("input_activity", "\"timestamp\" < @cutoff"),
        [RetentionTarget.IdleSessions] = ("idle_sessions", "COALESCE(ended_at, started_at) < @cutoff"),
        [RetentionTarget.FileEvents] = ("file_events", "\"timestamp\" < @cutoff"),
        [RetentionTarget.UsbEvents] = ("usb_events", "\"timestamp\" < @cutoff"),
        [RetentionTarget.ApplicationSessions] = ("application_sessions", "COALESCE(ended_at, started_at) < @cutoff"),
        [RetentionTarget.ProcessSessions] = ("process_sessions", "ended_at IS NOT NULL AND ended_at < @cutoff"),
        [RetentionTarget.UserSessions] = ("user_sessions", "logout_at IS NOT NULL AND logout_at < @cutoff"),
        [RetentionTarget.SystemMetrics] = ("system_metrics", "\"timestamp\" < @cutoff"),
        [RetentionTarget.Heartbeats] = ("heartbeats", "\"timestamp\" < @cutoff"),
        [RetentionTarget.SecurityEvents] = ("security_events", "raised_at < @cutoff AND resolved_at IS NOT NULL"),
        [RetentionTarget.AuditLogs] = ("audit_logs", "\"timestamp\" < @cutoff"),
        [RetentionTarget.RefreshTokens] = ("refresh_tokens", "expires_at < @cutoff"),
    };

    public async Task<long> DeleteOlderThanAsync(RetentionTarget target, DateTimeOffset cutoff, CancellationToken ct)
    {
        var (table, condition) = Map[target];
        var sql = $"DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM {table} WHERE {condition} LIMIT {BatchSize})";
        long total = 0;
        while (true)
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("cutoff", cutoff.ToUniversalTime())], ct);
            total += deleted;
            if (deleted < BatchSize) return total;
        }
    }
}
