using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Persistence;

/// <summary>key/value store in the agent_state table.</summary>
public sealed class SqliteAgentStateStore : IAgentStateStore
{
    private readonly SqliteDatabase _db;
    private readonly TimeProvider _time;

    public SqliteAgentStateStore(SqliteDatabase db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM agent_state WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    /// <inheritdoc />
    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        using var c = _db.Open();
        SqliteDatabase.Exec(c,
            "INSERT INTO agent_state(key,value,updated_utc) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value=$v, updated_utc=$t;",
            ("$k", key), ("$v", value), ("$t", _time.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture)));
        return Task.CompletedTask;
    }
}

/// <summary>Local bookkeeping tables used for restart continuity and diagnostics.</summary>
public sealed class LocalTelemetryStore
{
    private readonly SqliteDatabase _db;

    public LocalTelemetryStore(SqliteDatabase db) => _db = db;

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Open (not yet stopped) process sessions recorded before the agent last stopped.</summary>
    public IReadOnlyList<(Guid Id, ProcessPayload Payload)> LoadOpenProcessSessions()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,pid,parent_pid,name,executable_path,user_name,windows_session,started_utc FROM process_sessions WHERE ended_utc IS NULL;";
        var list = new List<(Guid, ProcessPayload)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var started = DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            list.Add((Guid.Parse(r.GetString(0)), new ProcessPayload(Guid.Parse(r.GetString(0)), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), started, null, null, null, false)));
        }
        return list;
    }

    /// <summary>Record a process start.</summary>
    public void ProcessStarted(ProcessPayload p)
    {
        using var c = _db.Open();
        SqliteDatabase.Exec(c, """
            INSERT OR REPLACE INTO process_sessions(id,pid,parent_pid,name,executable_path,user_name,windows_session,started_utc,ended_utc)
            VALUES($id,$pid,$ppid,$n,$path,$u,$ws,$s,NULL);
            """, ("$id", p.ProcessSessionId.ToString()), ("$pid", p.ProcessId), ("$ppid", p.ParentProcessId), ("$n", p.ProcessName),
            ("$path", p.ExecutablePath), ("$u", p.UserName), ("$ws", p.WindowsSessionId), ("$s", Iso(p.StartedUtc)));
    }

    /// <summary>Record a process stop.</summary>
    public void ProcessStopped(Guid id, DateTimeOffset endedUtc)
    {
        using var c = _db.Open();
        SqliteDatabase.Exec(c, "UPDATE process_sessions SET ended_utc=$e WHERE id=$id;", ("$e", Iso(endedUtc)), ("$id", id.ToString()));
        SqliteDatabase.Exec(c, "DELETE FROM process_sessions WHERE ended_utc IS NOT NULL AND ended_utc < $cutoff;", ("$cutoff", Iso(endedUtc.AddDays(-2))));
    }

    /// <summary>Record a closed foreground session (diagnostics view).</summary>
    public void ApplicationSessionClosed(ApplicationBackgroundPayload p)
    {
        using var c = _db.Open();
        SqliteDatabase.Exec(c, """
            INSERT OR REPLACE INTO application_sessions(id,process_name,executable_path,window_title,started_utc,ended_utc,duration_seconds,interaction_seconds,idle_seconds)
            VALUES($id,$n,$path,$t,$s,$e,$d,$i,$idle);
            """, ("$id", p.AppSessionId.ToString()), ("$n", p.ProcessName), ("$path", p.ExecutablePath), ("$t", p.WindowTitle),
            ("$s", Iso(p.StartedUtc)), ("$e", Iso(p.EndedUtc)), ("$d", p.DurationSeconds), ("$i", p.InteractionSeconds), ("$idle", p.IdleSeconds));
        SqliteDatabase.Exec(c, "DELETE FROM application_sessions WHERE started_utc < $cutoff;", ("$cutoff", Iso(p.EndedUtc.AddDays(-3))));
    }

    /// <summary>Keep the last metric samples for local diagnostics.</summary>
    public void RecordMetric(DateTimeOffset at, SystemMetricPayload m)
    {
        var diskUsed = m.Disks.Count > 0 ? m.Disks.Max(d => d.UsedPercent) : (double?)null;
        using var c = _db.Open();
        SqliteDatabase.Exec(c,
            "INSERT INTO system_metrics(timestamp_utc,cpu_percent,memory_used_percent,disk_used_percent,payload_json) VALUES($t,$c,$m,$d,$j);",
            ("$t", Iso(at)), ("$c", m.CpuPercent), ("$m", m.MemoryUsedPercent), ("$d", diskUsed), ("$j", JsonSerializer.Serialize(m, ProtocolJson.Options)));
    }

    /// <summary>Record a heartbeat attempt.</summary>
    public void RecordHeartbeat(DateTimeOffset sentUtc, bool success, int? httpStatus, string? error, long? rttMs, DateTimeOffset? serverTime, long offsetMs)
    {
        using var c = _db.Open();
        SqliteDatabase.Exec(c, """
            INSERT INTO heartbeats(sent_utc,success,http_status,error,rtt_ms,server_time_utc,clock_offset_ms)
            VALUES($s,$ok,$h,$e,$r,$st,$o);
            """, ("$s", Iso(sentUtc)), ("$ok", success ? 1 : 0), ("$h", httpStatus), ("$e", error), ("$r", rttMs),
            ("$st", serverTime is null ? null : Iso(serverTime.Value)), ("$o", offsetMs));
    }

    /// <summary>Latest heartbeat rows for diagnostics.</summary>
    public IReadOnlyList<(DateTimeOffset SentUtc, bool Success, string? Error, long? RttMs)> RecentHeartbeats(int count)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sent_utc,success,error,rtt_ms FROM heartbeats ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", count);
        var list = new List<(DateTimeOffset, bool, string?, long?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
                r.GetInt32(1) == 1, r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }
}
