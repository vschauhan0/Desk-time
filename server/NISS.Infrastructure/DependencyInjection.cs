using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Options;
using NISS.Infrastructure.Persistence;
using NISS.Infrastructure.Security;

namespace NISS.Infrastructure;

/// <summary>Registers persistence and security services.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNissInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>((sp, o) =>
        {
            var connection = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
            o.UseNpgsql(connection, npgsql => npgsql.CommandTimeout(60)).UseSnakeCaseNamingConvention();
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IIngestionStore, IngestionStore>();
        services.AddScoped<IDeviceNumberSequence, DeviceNumberSequence>();
        services.AddScoped<IRetentionStore, RetentionStore>();
        services.AddScoped<Application.Screenshots.IScreenshotStore, Storage.FileScreenshotStore>();

        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<ISecretHasher, SecretHasher>();
        services.AddSingleton<ISigningKeyProvider, SigningKeyProvider>();
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();
        return services;
    }

    /// <summary>Applies pending EF Core migrations. Returns the names of the migrations that were applied.</summary>
    public static async Task<IReadOnlyList<string>> MigrateDatabaseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("NISS.Migrations")
                .LogInformation("Applying {Count} pending database migration(s): {Names}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
        }
        return pending;
    }
}
