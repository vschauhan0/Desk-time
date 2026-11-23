using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Options;
using NISS.Application.Seeding;
using NISS.Infrastructure;

namespace NISS.Api.Hosting;

/// <summary>Migrations, seeding and signing-key preparation that must complete before the API serves requests.</summary>
public static class StartupTasks
{
    public static async Task InitializeAsync(this WebApplication app, CancellationToken ct = default)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NISS.Startup");
        if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup)
            await DependencyInjection.MigrateDatabaseAsync(app.Services, ct);

        await app.Services.GetRequiredService<ISigningKeyProvider>().InitializeAsync(ct);

        using var scope = app.Services.CreateScope();
        var seed = await scope.ServiceProvider.GetRequiredService<IDataSeeder>().SeedAsync(ct);
        if (seed.GeneratedAdminPassword is not null)
        {
            // Shown exactly once, on the console only: it is not written to the log files and only its hash is stored.
            Console.Error.WriteLine();
            Console.Error.WriteLine("==============================================================================");
            Console.Error.WriteLine(" NISS Monitor: initial administrator account created");
            Console.Error.WriteLine($"   username: {seed.BootstrapAdminUsername}");
            Console.Error.WriteLine($"   password: {seed.GeneratedAdminPassword}");
            Console.Error.WriteLine(" This password is shown ONLY NOW. Sign in and change it, or set");
            Console.Error.WriteLine(" Security:BootstrapAdminPassword before first start.");
            Console.Error.WriteLine("==============================================================================");
            Console.Error.WriteLine();
        }
        log.LogInformation("Startup tasks completed");
    }

    /// <summary>Handles <c>--migrate</c>: apply migrations and exit.</summary>
    public static async Task<int> MigrateAndExitAsync(this WebApplication app)
    {
        var applied = await DependencyInjection.MigrateDatabaseAsync(app.Services);
        Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}");
        return 0;
    }
}
