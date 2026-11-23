using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Infrastructure;
using NISS.Monitor.Infrastructure.Collectors;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Infrastructure.Runtime;
using NISS.Monitor.Service.Logging;
using NISS.Monitor.Service.Workers;
using Serilog;
using Serilog.Events;

namespace NISS.Monitor.Service;

/// <summary>Entry point of NISSMonitor.Service.exe (Windows Service; also runs as a console host for development).</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

        builder.Configuration
            .AddJsonFile("agentsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("agentsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NISS_");

        var settings = builder.Configuration.GetSection(AgentSettings.SectionName).Get<AgentSettings>() ?? new AgentSettings();
        var paths = AgentPaths.Resolve(settings);
        paths.EnsureCreated();

        builder.Services.AddWindowsService(o => o.ServiceName = AgentConstants.ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

        ConfigureLogging(builder, paths);
        builder.Services.AddNissAgentInfrastructure(builder.Configuration);
        RegisterWorkers(builder.Services);

        var host = builder.Build();
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "NISS Monitor service terminated unexpectedly");
            return 1; // non-zero exit lets Windows service recovery restart us
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, AgentPaths paths)
    {
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((_, lc) => lc
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.With(new RedactionEnricher())
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true, shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(), writeToProviders: true);

        if (WindowsServiceHelpers.IsWindowsService())
        {
            // Windows Event Log: warnings/errors plus the lifecycle category.
            builder.Logging.AddEventLog(o => { o.SourceName = AgentConstants.EventLogSource; o.LogName = "Application"; });
            builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Warning);
        }
    }

    private static void RegisterWorkers(IServiceCollection services)
    {
        // Registration order = start order; hosted services stop in reverse, so collectors stop first and the
        // lifecycle worker (which reports AGENT_STOPPED) stops last.
        services.AddHostedService<AgentLifecycleWorker>();
        services.AddHostedService<EventPipelineWorker>();
        services.AddHostedService<UploadWorker>();
        services.AddHostedService<HeartbeatWorker>();
        services.AddHostedService<PeerWatchdogWorker>();
        services.AddHostedService<AgentStatusWorker>();
        services.AddHostedService<QueueMaintenanceWorker>();
        services.AddHostedService<SystemMetricsWorker>();

        AddCollector<IProcessMonitor>(services);
        AddCollector<WtsSessionMonitor>(services);
        AddCollector<SessionPipeServer>(services);
        AddCollector<SessionLauncher>(services);
        AddCollector<NetworkMonitor>(services);
        AddCollector<IUsbMonitor>(services);
        AddCollector<IFileActivityMonitor>(services);
    }

    private static void AddCollector<T>(IServiceCollection services) where T : class, ICollector
        => services.AddSingleton<IHostedService>(sp => new CollectorWorker(
            sp.GetRequiredService<T>(), sp.GetRequiredService<DeviceIdentityStore>(), sp.GetRequiredService<ICollectorRegistry>(),
            sp.GetRequiredService<IEventSink>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>()));
}
