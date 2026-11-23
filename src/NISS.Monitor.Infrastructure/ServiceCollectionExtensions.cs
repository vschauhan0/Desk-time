using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Collectors;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Infrastructure.Runtime;

namespace NISS.Monitor.Infrastructure;

/// <summary>Composition root helpers shared by the Windows service and the admin CLI.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register storage, identity, transport and all collectors.</summary>
    public static IServiceCollection AddNissAgentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentSettings>(configuration.GetSection(AgentSettings.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => AgentPaths.Resolve(sp.GetRequiredService<IOptions<AgentSettings>>().Value));

        // storage
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<SqliteEventQueue>();
        services.AddSingleton<IEventQueue>(sp => sp.GetRequiredService<SqliteEventQueue>());
        services.AddSingleton<IAgentStateStore, SqliteAgentStateStore>();
        services.AddSingleton<LocalTelemetryStore>();

        // identity + shared runtime state
        services.AddSingleton<DeviceIdentityStore>();
        services.AddSingleton<MachineInfo>();
        services.AddSingleton<ServerTrust>();
        services.AddSingleton<ClockOffsetTracker>();
        services.AddSingleton<SequenceGenerator>();
        services.AddSingleton<AgentGate>();
        services.AddSingleton<LatestMetrics>();
        services.AddSingleton<IPolicyProvider, PolicyProvider>();
        services.AddSingleton<ILiveStateStore, LiveStateStore>();
        services.AddSingleton<ICollectorRegistry, CollectorRegistry>();
        services.AddSingleton<IServiceStateController, Setup.WindowsServiceStateController>();
        services.AddSingleton<ChannelEventSink>();
        services.AddSingleton<IEventSink>(sp => sp.GetRequiredService<ChannelEventSink>());
        services.AddSingleton<IEventFactory, EventFactory>();

        // transport: HTTPS with optional certificate pinning
        services.AddHttpClient<ServerApiClient>()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var trust = sp.GetRequiredService<ServerTrust>();
                return new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    SslOptions =
                    {
                        RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                            cert is not null && trust.Validate(cert as X509Certificate2 ?? new X509Certificate2(cert), errors),
                    },
                };
            });
        services.AddSingleton<IUploader, Uploader>();
        services.AddSingleton<HeartbeatService>();
        services.AddSingleton<IHeartbeatService>(sp => sp.GetRequiredService<HeartbeatService>());
        services.AddSingleton<IDeviceEnrollmentService, DeviceEnrollmentService>();

        // collectors (service side)
        services.AddSingleton<ISystemMetricsCollector, SystemMetricsCollector>();
        services.AddSingleton<IProcessMonitor, ProcessMonitor>();
        services.AddSingleton<IUsbMonitor, UsbMonitor>();
        services.AddSingleton<IFileActivityMonitor, FileActivityMonitor>();
        services.AddSingleton<NetworkMonitor>();
        services.AddSingleton<WtsSessionMonitor>();
        services.AddSingleton<SessionPipeServer>();
        services.AddSingleton<SessionLauncher>();

        // V1 placeholders for future modules
        services.AddSingleton<IScreenCaptureService, DisabledScreenCaptureService>();
        services.AddSingleton<IBrowserMonitoringProvider, NotEnabledBrowserMonitoringProvider>();
        return services;
    }
}
