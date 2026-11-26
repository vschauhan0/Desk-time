using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace NISS.Api.Tests.Infrastructure;

/// <summary>Serialises all database-backed test classes: they share one PostgreSQL database.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollection
{
    public const string Name = "Api";
}

/// <summary>
/// Hosts the real API (TestServer) against the PostgreSQL test database with a controllable clock.
/// The database is emptied when the factory starts (unless <c>resetDatabase</c> is false).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminPassword = "Test-Admin-Passw0rd!";

    private readonly bool _resetDatabase;
    private readonly Dictionary<string, string?> _settings;

    /// <summary>The clock the server uses. Starts at the real current time.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public ApiFactory() : this(true, null) { }

    internal ApiFactory(bool resetDatabase, Dictionary<string, string?>? overrides)
    {
        _resetDatabase = resetDatabase;
        _settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = TestDatabase.ConnectionString,
            ["Database:ApplyMigrationsOnStartup"] = "true",
            ["Security:BootstrapAdminUsername"] = "admin",
            ["Security:BootstrapAdminPassword"] = AdminPassword,
            ["Security:JwtSigningKey"] = "",
            ["RateLimits:LoginPerMinute"] = "1000",
            ["RateLimits:EnrollPerMinute"] = "1000",
            ["RateLimits:IngestPerMinute"] = "100000",
            ["Workers:Enabled"] = "false",
            ["ScreenshotStorage:Root"] = Path.Combine(Path.GetTempPath(), "niss-shots-test"),
        };
        if (overrides is not null) foreach (var (key, value) in overrides) _settings[key] = value;
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (_resetDatabase) await TestDatabase.ResetAsync();
        _ = Server;   // start the host (migrations + seeding)
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public IServiceScope Scope() => Services.CreateScope();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<object>>> _once = new();

    /// <summary>Runs <paramref name="seed"/> once per factory (xUnit creates the test class per test, the factory is shared).</summary>
    public async Task<T> OnceAsync<T>(string key, Func<Task<T>> seed) where T : notnull =>
        (T)await _once.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await seed())).Value;
}
