using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Setup;
using Serilog;

namespace NISS.Monitor.Watchdog;

/// <summary>
/// NISSMonitor.Watchdog.exe - the smaller half of the mutual watchdog pair. Its only job is watching the main NISS
/// Monitor service and restarting it if it is ever found stopped; the main service runs the mirror-image worker
/// (PeerWatchdogWorker) to do the same for this one. See <c>PeerServiceWatchdog</c> for exactly what this
/// resilience covers (a casual stop of either service) and does not cover (a deliberate admin disabling both).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(o => o.ServiceName = AgentConstants.WatchdogServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

        builder.Logging.ClearProviders();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NISS", "Agent", "Logs");
        try { Directory.CreateDirectory(dataDir); } catch (IOException) { dataDir = Path.GetTempPath(); }
        builder.Services.AddSerilog((_, lc) => lc
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(dataDir, "watchdog-.log"), rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7, fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console());
        if (WindowsServiceHelpers.IsWindowsService())
            builder.Logging.AddEventLog(o => { o.SourceName = AgentConstants.WatchdogEventLogSource; o.LogName = "Application"; });

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IServiceStateController, WindowsServiceStateController>();
        builder.Services.AddHostedService<WatchdogWorker>();

        var host = builder.Build();
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "NISS Monitor Watchdog terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }
}
