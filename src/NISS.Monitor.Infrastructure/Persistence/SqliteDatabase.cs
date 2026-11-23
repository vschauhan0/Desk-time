using Microsoft.Data.Sqlite;
using NISS.Monitor.Core.Configuration;

namespace NISS.Monitor.Infrastructure.Persistence;

/// <summary>Owns the agent SQLite file (C:\ProgramData\NISS\Agent\agent.db) and its schema.</summary>
public sealed class SqliteDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly object _initGate = new();
    private bool _initialized;

    public SqliteDatabase(AgentPaths paths)
    {
        Path = paths.DatabasePath;
        paths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Database file path.</summary>
    public string Path { get; }

    /// <summary>Open a connection (schema is created on first use).</summary>
    public SqliteConnection Open()
    {
        EnsureInitialized();
        return OpenRaw();
    }

    /// <summary>Force schema creation (used by <c>NISSAgent --init</c> and the installer).</summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initGate)
        {
            if (_initialized) return;
            using var c = OpenRaw();
            Exec(c, "PRAGMA journal_mode=WAL;");
            Exec(c, Schema);
            Exec(c, $"INSERT OR IGNORE INTO agent_state(key,value,updated_utc) VALUES('schema_version','{SchemaVersion}',strftime('%Y-%m-%dT%H:%M:%fZ','now'));");
            _initialized = true;
        }
    }

    private SqliteConnection OpenRaw()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
        return c;
    }

    internal static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            event_id       TEXT PRIMARY KEY,
            sequence       INTEGER NOT NULL UNIQUE,
            event_type     TEXT NOT NULL,
            timestamp_utc  TEXT NOT NULL,
            local_timestamp TEXT NOT NULL,
            envelope_json  TEXT NOT NULL,
            created_utc    TEXT NOT NULL,
            uploaded_utc   TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_uploaded ON events(uploaded_utc);

        CREATE TABLE IF NOT EXISTS upload_queue (
            event_id         TEXT PRIMARY KEY,
            sequence         INTEGER NOT NULL,
            attempts         INTEGER NOT NULL DEFAULT 0,
            next_attempt_utc TEXT NULL,
            last_error       TEXT NULL,
            dead_letter      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_upload_queue_order ON upload_queue(dead_letter, sequence);

        CREATE TABLE IF NOT EXISTS agent_state (
            key         TEXT PRIMARY KEY,
            value       TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS device_identity (
            id                INTEGER PRIMARY KEY CHECK (id = 1),
            device_id         TEXT NOT NULL,
            secret_protected  BLOB NOT NULL,
            server_base_url   TEXT NOT NULL,
            employee_id       TEXT NULL,
            cert_thumbprint   TEXT NULL,
            enrolled_utc      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS application_sessions (
            id                  TEXT PRIMARY KEY,
            process_name        TEXT NOT NULL,
            executable_path     TEXT NULL,
            window_title        TEXT NULL,
            started_utc         TEXT NOT NULL,
            ended_utc           TEXT NULL,
            duration_seconds    REAL NOT NULL DEFAULT 0,
            interaction_seconds REAL NOT NULL DEFAULT 0,
            idle_seconds        REAL NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_app_sessions_started ON application_sessions(started_utc);

        CREATE TABLE IF NOT EXISTS process_sessions (
            id              TEXT PRIMARY KEY,
            pid             INTEGER NOT NULL,
            parent_pid      INTEGER NULL,
            name            TEXT NOT NULL,
            executable_path TEXT NULL,
            user_name       TEXT NULL,
            windows_session INTEGER NOT NULL DEFAULT 0,
            started_utc     TEXT NOT NULL,
            ended_utc       TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_process_sessions_open ON process_sessions(ended_utc);

        CREATE TABLE IF NOT EXISTS heartbeats (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            sent_utc         TEXT NOT NULL,
            success          INTEGER NOT NULL,
            http_status      INTEGER NULL,
            error            TEXT NULL,
            rtt_ms           INTEGER NULL,
            server_time_utc  TEXT NULL,
            clock_offset_ms  INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS system_metrics (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc       TEXT NOT NULL,
            cpu_percent         REAL NOT NULL,
            memory_used_percent REAL NOT NULL,
            disk_used_percent   REAL NULL,
            payload_json        TEXT NOT NULL
        );
        """;
}
