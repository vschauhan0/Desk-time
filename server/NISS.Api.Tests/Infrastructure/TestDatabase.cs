using Microsoft.EntityFrameworkCore;
using Npgsql;
using NISS.Infrastructure.Persistence;

namespace NISS.Api.Tests.Infrastructure;

/// <summary>Prepares the PostgreSQL test database: applies migrations and empties every table.</summary>
public static class TestDatabase
{
    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("NISS_TEST_DB") ?? "Host=127.0.0.1;Port=5433;Database=niss_monitor_test;Username=postgres";

    /// <summary>Applies migrations, truncates all data tables and restarts the device numbering.</summary>
    public static async Task ResetAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options;
        await using (var db = new AppDbContext(options)) await db.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory'", connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        var sql = $"TRUNCATE TABLE {string.Join(", ", tables.Select(t => $"\"{t}\""))} RESTART IDENTITY CASCADE; ALTER SEQUENCE device_number_seq RESTART WITH 1;";
        await using var truncate = new NpgsqlCommand(sql, connection);
        await truncate.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a scalar query against the test database.</summary>
    public static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
