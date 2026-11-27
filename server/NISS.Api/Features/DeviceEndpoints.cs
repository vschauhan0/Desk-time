using NISS.Api.Http;
using System.Text.Json;
using NISS.Api.Security;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Devices;
using NISS.Application.Live;
using NISS.Application.Reporting;
using NISS.Monitor.Protocol;

namespace NISS.Api.Features;

/// <summary>Dashboard endpoints for devices, live monitoring and cross-device application/process views.</summary>
public static class DeviceEndpoints
{
    public static void MapDevices(this RouteGroupBuilder api)
    {
        var viewer = api.MapGroup("").RequireAuthorization(AuthDefaults.ViewerPolicy);

        viewer.MapGet("/overview", async (IOverviewService overview, CancellationToken ct) => Results.Ok(await overview.GetAsync(ct)));

        viewer.MapGet("/live", async ([AsParameters] LiveQuery q, ILiveStateService live, CancellationToken ct) =>
            Results.Ok(await live.QueryAsync(new LiveFilter(q.Status, q.Department, q.Employee, q.Search), ct)));

        var devices = viewer.MapGroup("/devices");
        devices.MapGet("", async ([AsParameters] DeviceListQuery q, IDeviceQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(new DeviceFilter(q.Status, q.Employee, q.Department, q.Search), q.Paging, ct)));

        devices.MapGet("/{deviceId}", async (string deviceId, IDeviceQueryService svc, CancellationToken ct) => Results.Ok(await svc.GetDetailAsync(deviceId, ct)));

        devices.MapGet("/{deviceId}/activity", async (string deviceId, [AsParameters] ActivityQuery q, IDeviceQueryService svc, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await svc.ActivityAsync(deviceId, TimeRange.Resolve(q.From, q.To, clock), q.EventType ?? [], q.Paging, ct)));

        devices.MapGet("/{deviceId}/applications", async (string deviceId, [AsParameters] ApplicationsQuery q, IDeviceQueryService svc,
            IApplicationUsageService usage, TimeProvider clock, CancellationToken ct) =>
        {
            var id = await svc.ResolveIdAsync(deviceId, ct);
            var filter = new UsageFilter(Scope.ForDevice(id), q.Application, q.Category);
            return Results.Ok(await usage.GetActivityAsync(filter, TimeRange.Resolve(q.From, q.To, clock), q.Paging, ct));
        });

        devices.MapGet("/{deviceId}/processes", async (string deviceId, [AsParameters] ProcessesQuery q, IDeviceQueryService svc,
            IApplicationUsageService usage, TimeProvider clock, CancellationToken ct) =>
        {
            var id = await svc.ResolveIdAsync(deviceId, ct);
            return Results.Ok(await usage.GetProcessesAsync(new ProcessFilter(Scope.ForDevice(id), q.Status, q.Search), TimeRange.Resolve(q.From, q.To, clock), q.Paging, ct));
        });

        devices.MapGet("/{deviceId}/metrics", async (string deviceId, [AsParameters] MetricsQuery q, IDeviceQueryService svc, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await svc.MetricsAsync(deviceId, TimeRange.Resolve(q.From, q.To, clock), q.MaxPoints, ct)));

        devices.MapGet("/{deviceId}/timeline", async (string deviceId, [AsParameters] RangeQuery q, IDeviceQueryService svc,
            ITimelineService timeline, TimeProvider clock, CancellationToken ct) =>
        {
            var id = await svc.ResolveIdAsync(deviceId, ct);
            return Results.Ok(await timeline.GetAsync(Scope.ForDevice(id), TimeRange.Resolve(q.From, q.To, clock), ct));
        });

        devices.MapGet("/{deviceId}/diagnostics", async (string deviceId, IDeviceQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.DiagnosticsAsync(deviceId, ct)));

        devices.MapGet("/{deviceId}/inventory", async (string deviceId, IDeviceQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.InventoryAsync(deviceId, ct)));

        var admin = api.MapGroup("/devices").RequireAuthorization(AuthDefaults.AdminPolicy);

        admin.MapGet("/{deviceId}/diagnostics/download", async (string deviceId, IDeviceQueryService svc, IAuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var diagnostics = await svc.DiagnosticsAsync(deviceId, ct);
            await audit.WriteAsync(AuditActions.DiagnosticsDownloaded, diagnostics.DeviceId, null, ct);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { generatedAtUtc = clock.GetUtcNow(), diagnostics }, DownloadJson);
            return Results.File(bytes, "application/json", $"diagnostics-{diagnostics.DeviceId}.json");
        });

        admin.MapPost("/{deviceId}/disable", async (string deviceId, IDeviceCommandService commands, CancellationToken ct) =>
        {
            await commands.DisableAsync(deviceId, ct);
            return Results.NoContent();
        }).RequirePageEdit("devices");
        admin.MapPost("/{deviceId}/enable", async (string deviceId, IDeviceCommandService commands, CancellationToken ct) =>
        {
            await commands.EnableAsync(deviceId, ct);
            return Results.NoContent();
        }).RequirePageEdit("devices");
        admin.MapPost("/{deviceId}/revoke", async (string deviceId, IDeviceCommandService commands, CancellationToken ct) =>
        {
            await commands.RevokeAsync(deviceId, ct);
            return Results.NoContent();
        }).RequirePageEdit("devices");
        admin.MapPost("/{deviceId}/re-enroll", async (string deviceId, IDeviceCommandService commands, CancellationToken ct) =>
            Results.Ok(await commands.ReEnrollAsync(deviceId, 24, ct))).RequirePageEdit("devices");

        // Cross-device views.
        viewer.MapGet("/applications", async ([AsParameters] ApplicationsQuery q, IScopeResolver scopes, IApplicationUsageService usage, TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(q.Device, q.Employee, q.Department, ct);
            return Results.Ok(await usage.GetActivityAsync(new UsageFilter(scope, q.Application, q.Category), TimeRange.Resolve(q.From, q.To, clock), q.Paging, ct));
        });

        viewer.MapGet("/processes", async ([AsParameters] ProcessesQuery q, IScopeResolver scopes, IApplicationUsageService usage, TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(q.Device, q.Employee, q.Department, ct);
            return Results.Ok(await usage.GetProcessesAsync(new ProcessFilter(scope, q.Status, q.Search), TimeRange.Resolve(q.From, q.To, clock), q.Paging, ct));
        });
    }

    private static readonly JsonSerializerOptions DownloadJson = new(ProtocolJson.Options) { WriteIndented = true };
}
