using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Persistence;

/// <summary>
/// Durable event queue. <c>events</c> holds every envelope; <c>upload_queue</c> tracks what still has to be sent.
/// Ordering is by <c>sequence</c>, duplicates are impossible (event_id is the primary key), and permanently rejected
/// events are dead-lettered so they cannot block the queue.
/// </summary>
public sealed class SqliteEventQueue : IEventQueue
{
    private static readonly string[] LowValueTypes = [EventTypes.MouseActivity, EventTypes.KeyboardActivity, EventTypes.SystemMetric];
    private readonly SqliteDatabase _db;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteEventQueue(SqliteDatabase db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
    private static DateTimeOffset? Parse(object? o) => o is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(EventEnvelope envelope, CancellationToken cancellationToken)
        => new(EnqueueBatchAsync([envelope], cancellationToken));

    /// <inheritdoc />
    public async Task EnqueueBatchAsync(IReadOnlyList<EventEnvelope> envelopes, CancellationToken cancellationToken)
    {
        if (envelopes.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            var now = Iso(_time.GetUtcNow());
            foreach (var e in envelopes)
            {
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR IGNORE INTO events(event_id, sequence, event_type, timestamp_utc, local_timestamp, envelope_json, created_utc)
                    VALUES($id,$seq,$type,$ts,$lts,$json,$now);
                    """;
                ins.Parameters.AddWithValue("$id", e.EventId.ToString());
                ins.Parameters.AddWithValue("$seq", e.Sequence);
                ins.Parameters.AddWithValue("$type", e.EventType);
                ins.Parameters.AddWithValue("$ts", Iso(e.TimestampUtc));
                ins.Parameters.AddWithValue("$lts", e.LocalTimestamp.ToString("o", CultureInfo.InvariantCulture));
                ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(e, ProtocolJson.Options));
                ins.Parameters.AddWithValue("$now", now);
                var inserted = ins.ExecuteNonQuery();
                if (inserted == 0) continue; // same event id already stored: keep exactly one copy

                using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "INSERT OR IGNORE INTO upload_queue(event_id, sequence) VALUES($id,$seq);";
                q.Parameters.AddWithValue("$id", e.EventId.ToString());
                q.Parameters.AddWithValue("$seq", e.Sequence);
                q.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueuedEvent>> PeekBatchAsync(int max, CancellationToken cancellationToken)
    {
        using var c = _db.Open();
        var now = Iso(_time.GetUtcNow());

        // Strict ordering: if the oldest pending event is still backing off, nothing may overtake it.
        using (var head = c.CreateCommand())
        {
            head.CommandText = "SELECT next_attempt_utc FROM upload_queue WHERE dead_letter = 0 ORDER BY sequence ASC LIMIT 1;";
            var next = head.ExecuteScalar() as string;
            if (next is not null && string.CompareOrdinal(next, now) > 0)
                return Task.FromResult<IReadOnlyList<QueuedEvent>>([]);
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT q.event_id, q.sequence, e.event_type, q.attempts, e.envelope_json
            FROM upload_queue q JOIN events e ON e.event_id = q.event_id
            WHERE q.dead_letter = 0
            ORDER BY q.sequence ASC
            LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max", max);
        var list = new List<QueuedEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new QueuedEvent(Guid.Parse(r.GetString(0)), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
        return Task.FromResult<IReadOnlyList<QueuedEvent>>(list);
    }

    /// <inheritdoc />
    public async Task MarkUploadedAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            var now = Iso(_time.GetUtcNow());
            foreach (var id in eventIds)
            {
                SqliteDatabase.Exec(c, "UPDATE events SET uploaded_utc=$now WHERE event_id=$id;", ("$now", now), ("$id", id.ToString()));
                SqliteDatabase.Exec(c, "DELETE FROM upload_queue WHERE event_id=$id;", ("$id", id.ToString()));
            }
            SqliteDatabase.Exec(c, "INSERT INTO agent_state(key,value,updated_utc) VALUES('last_upload_utc',$v,$v) ON CONFLICT(key) DO UPDATE SET value=$v, updated_utc=$v;", ("$v", now));
            SqliteDatabase.Exec(c, "DELETE FROM agent_state WHERE key='last_upload_error';");
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc />
    public async Task MarkDeadLetterAsync(IReadOnlyCollection<(Guid EventId, string? Error)> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var (id, err) in events)
                SqliteDatabase.Exec(c, "UPDATE upload_queue SET dead_letter=1, last_error=$e WHERE event_id=$id;", ("$e", err), ("$id", id.ToString()));
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(IReadOnlyCollection<Guid> eventIds, string error, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            var next = Iso(_time.GetUtcNow() + retryAfter);
            foreach (var id in eventIds)
                SqliteDatabase.Exec(c, "UPDATE upload_queue SET attempts=attempts+1, next_attempt_utc=$n, last_error=$e WHERE event_id=$id;", ("$n", next), ("$e", error), ("$id", id.ToString()));
            var now = Iso(_time.GetUtcNow());
            SqliteDatabase.Exec(c, "INSERT INTO agent_state(key,value,updated_utc) VALUES('last_upload_error',$v,$now) ON CONFLICT(key) DO UPDATE SET value=$v, updated_utc=$now;", ("$v", error), ("$now", now));
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc />
    public Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        using var c = _db.Open();
        int pending, dead;
        object? oldest;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM upload_queue WHERE dead_letter = 0),
                  (SELECT COUNT(*) FROM upload_queue WHERE dead_letter = 1),
                  (SELECT e.timestamp_utc FROM upload_queue q JOIN events e ON e.event_id=q.event_id WHERE q.dead_letter=0 ORDER BY q.sequence LIMIT 1);
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            pending = r.GetInt32(0); dead = r.GetInt32(1); oldest = r.IsDBNull(2) ? null : r.GetString(2);
        }
        var lastUpload = ReadState(c, "last_upload_utc");
        var lastError = ReadState(c, "last_upload_error");
        return Task.FromResult(new QueueStats(pending, dead, Parse(oldest), Parse(lastUpload), lastError));
    }

    /// <inheritdoc />
    public Task<long> GetMaxSequenceAsync(CancellationToken cancellationToken)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM events;";
        return Task.FromResult(Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task MaintainAsync(TimeSpan keepUploaded, int maxPending, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var c = _db.Open();
            SqliteDatabase.Exec(c, "DELETE FROM events WHERE uploaded_utc IS NOT NULL AND uploaded_utc < $cutoff;", ("$cutoff", Iso(_time.GetUtcNow() - keepUploaded)));
            SqliteDatabase.Exec(c, "DELETE FROM events WHERE event_id IN (SELECT event_id FROM upload_queue WHERE dead_letter=1) AND created_utc < $cutoff;", ("$cutoff", Iso(_time.GetUtcNow() - TimeSpan.FromDays(7))));
            SqliteDatabase.Exec(c, "DELETE FROM upload_queue WHERE dead_letter=1 AND event_id NOT IN (SELECT event_id FROM events);");
            SqliteDatabase.Exec(c, "DELETE FROM system_metrics WHERE id NOT IN (SELECT id FROM system_metrics ORDER BY id DESC LIMIT 2000);");
            SqliteDatabase.Exec(c, "DELETE FROM heartbeats WHERE id NOT IN (SELECT id FROM heartbeats ORDER BY id DESC LIMIT 1000);");

            // Storage cap: when the backlog is enormous drop the lowest-value telemetry first.
            using var count = c.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM upload_queue WHERE dead_letter=0;";
            var pending = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (pending > maxPending)
            {
                var over = pending - maxPending;
                var types = string.Join(",", LowValueTypes.Select(t => $"'{t}'"));
                SqliteDatabase.Exec(c, $"""
                    DELETE FROM upload_queue WHERE event_id IN (
                        SELECT q.event_id FROM upload_queue q JOIN events e ON e.event_id=q.event_id
                        WHERE q.dead_letter=0 AND e.event_type IN ({types}) ORDER BY q.sequence ASC LIMIT {over});
                    """);
            }
        }
        finally { _writeGate.Release(); }
    }

    private static string? ReadState(SqliteConnection c, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM agent_state WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
