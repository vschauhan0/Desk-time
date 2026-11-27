using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Auth;
using NISS.Application.Categories;
using NISS.Application.Devices;
using NISS.Application.Employees;
using NISS.Application.Health;
using NISS.Application.Ingestion;
using NISS.Application.Ingestion.Projectors;
using NISS.Application.Live;
using NISS.Application.Policies;
using NISS.Application.Reporting;
using NISS.Application.Reports;
using NISS.Application.Retention;
using NISS.Application.Seeding;
using NISS.Application.Settings;

namespace NISS.Application;

/// <summary>Registers the application layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNissApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // Cross-cutting
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<ISettingsAdminService, SettingsAdminService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IPolicyService, PolicyService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<INotificationBuffer, NotificationBuffer>();
        services.AddScoped<ISecurityEventService, SecurityEventService>();
        services.AddScoped<IDeviceStatusService, DeviceStatusService>();
        services.AddScoped<IDeviceHealthService, DeviceHealthService>();
        services.AddScoped<ILiveStateService, LiveStateService>();
        services.AddScoped<IDataSeeder, DataSeeder>();
        services.AddScoped<Updates.IAgentPackageService, Updates.AgentPackageService>();
        services.AddScoped<Updates.IAgentUpdateService, Updates.AgentUpdateService>();

        // Agent side
        services.AddScoped<IEnrollmentService, EnrollmentService>();
        services.AddScoped<IDeviceAuthenticator, DeviceAuthenticator>();
        services.AddScoped<IDeviceCommandService, DeviceCommandService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IEmployeeResolver, EmployeeResolver>();
        services.AddScoped<ILiveStateWriter, LiveStateWriter>();
        services.AddScoped<IEventIngestionService, EventIngestionService>();
        services.AddScoped<Screenshots.IScreenshotIngestionService, Screenshots.ScreenshotIngestionService>();
        services.AddScoped<Screenshots.IScreenshotQueryService, Screenshots.ScreenshotQueryService>();
        services.AddSingleton<Live.LiveSessionRegistry>();
        services.AddScoped<Live.ILiveViewService, Live.LiveViewService>();
        foreach (var projector in new[]
        {
            typeof(ProcessProjector), typeof(ApplicationSessionProjector), typeof(UserSessionProjector), typeof(IdleSessionProjector),
            typeof(InputActivityProjector), typeof(UsbProjector), typeof(FileProjector), typeof(SystemMetricProjector),
            typeof(NetworkProjector), typeof(AgentEventProjector), typeof(DeviceInventoryProjector),
        })
            services.AddScoped(typeof(IEventProjector), projector);

        // Dashboard side
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAdminUserService, AdminUserService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IDeviceQueryService, DeviceQueryService>();
        services.AddScoped<IAccessScopeProvider, AccessScopeProvider>();
        services.AddScoped<IScopeResolver, ScopeResolver>();
        services.AddScoped<IActivityLoader, ActivityLoader>();
        services.AddScoped<IApplicationUsageService, ApplicationUsageService>();
        services.AddScoped<ITimelineService, TimelineService>();
        services.AddScoped<IEventQueryService, EventQueryService>();
        services.AddScoped<IOverviewService, OverviewService>();
        services.AddScoped<IReportService, ReportService>();
        foreach (var generator in new[]
        {
            typeof(DailyReport), typeof(ApplicationUsageReport), typeof(ActiveIdleReport), typeof(AttendanceReport), typeof(DeviceHealthReport),
            typeof(ProcessTimelineReport), typeof(UsbReport), typeof(AgentHealthReport), typeof(DepartmentSummaryReport),
        })
            services.AddScoped(typeof(IReportGenerator), generator);

        // Maintenance
        services.AddScoped<IRetentionService, RetentionService>();
        services.AddScoped<IEnrollmentTokenCleanupService, EnrollmentTokenCleanupService>();
        return services;
    }
}
