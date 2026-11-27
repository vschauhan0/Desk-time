using Microsoft.Extensions.FileProviders;
using NISS.Application.Options;

namespace NISS.Api.Hosting;

/// <summary>Configuration sources: serversettings.json, optional serversettings.local.json (secrets, git-ignored), NISS_ environment variables.</summary>
public static class ConfigurationExtensions
{
    public static void AddNissConfiguration(this WebApplicationBuilder builder, string[] args)
    {
        var config = builder.Configuration;
        var baseDir = new PhysicalFileProvider(AppContext.BaseDirectory);
        config.AddJsonFile(baseDir, "serversettings.json", optional: true, reloadOnChange: false);
        config.AddJsonFile(baseDir, "serversettings.local.json", optional: true, reloadOnChange: false);
        var contentRoot = builder.Environment.ContentRootPath;
        if (!string.Equals(Path.GetFullPath(contentRoot), Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        {
            config.AddJsonFile(new PhysicalFileProvider(contentRoot), "serversettings.json", optional: true, reloadOnChange: false);
            config.AddJsonFile(new PhysicalFileProvider(contentRoot), "serversettings.local.json", optional: true, reloadOnChange: false);
        }
        config.AddEnvironmentVariables("NISS_");
        config.AddCommandLine(args);
    }

    /// <summary>Binds every options section. Values are read lazily so tests and hosts can override configuration late.</summary>
    public static void AddNissOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServerOptions>().Bind(configuration.GetSection(ServerOptions.Section));
        services.AddOptions<DashboardOptions>().Bind(configuration.GetSection(DashboardOptions.Section));
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Database:ConnectionString is required.");
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.Section));
        services.AddOptions<MonitoringOptions>().Bind(configuration.GetSection(MonitoringOptions.Section));
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.Section));
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.Section));
        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection(WorkerOptions.Section));
        services.AddOptions<ScreenshotStorageOptions>().Bind(configuration.GetSection(ScreenshotStorageOptions.Section));
        services.AddOptions<ScreenLiveOptions>().Bind(configuration.GetSection(ScreenLiveOptions.Section));
        services.AddOptions<AgentUpdateOptions>().Bind(configuration.GetSection(AgentUpdateOptions.Section));
        services.AddSingleton<NISS.Api.Features.PackageDownloadGate>();
    }
}
