using NISS.Api.Security;
using NISS.Application.Audit;
using NISS.Application.Categories;
using NISS.Application.Common;
using NISS.Application.Employees;
using NISS.Application.Health;
using NISS.Application.Policies;
using NISS.Application.Reporting;
using NISS.Application.Reports;
using NISS.Application.Settings;
using NISS.Monitor.Protocol;
using NISS.Api.Http;

namespace NISS.Api.Features;

/// <summary>/employees, /departments.</summary>
public static class EmployeeEndpoints
{
    public static void MapEmployees(this RouteGroupBuilder api)
    {
        var viewer = api.MapGroup("").RequireAuthorization(AuthDefaults.ViewerPolicy);
        var employees = viewer.MapGroup("/employees");

        employees.MapGet("", async ([AsParameters] EmployeeListQuery q, IEmployeeService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(q.Search, q.Department, q.Paging, ct)));
        employees.MapGet("/{id:guid}", async (Guid id, IEmployeeService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(id, ct)));
        employees.MapGet("/{id:guid}/summary", async (Guid id, [AsParameters] SummaryQuery q, IEmployeeService svc, CancellationToken ct) =>
            Results.Ok(await svc.DaySummaryAsync(id, q.Date, q.TzOffsetMinutes, ct)));
        employees.MapGet("/{id:guid}/timeline", async (Guid id, [AsParameters] RangeQuery q, IEmployeeService svc, ITimelineService timeline, TimeProvider clock, CancellationToken ct) =>
        {
            await svc.GetAsync(id, ct);   // 404 for unknown employees
            return Results.Ok(await timeline.GetAsync(Scope.ForEmployee(id), TimeRange.Resolve(q.From, q.To, clock), ct));
        });
        viewer.MapGet("/departments", async (IEmployeeService svc, CancellationToken ct) => Results.Ok(await svc.DepartmentsAsync(ct)));

        var admin = api.MapGroup("/employees").RequireAuthorization(AuthDefaults.AdminPolicy);
        admin.MapPost("", async (EmployeeUpsert body, IEmployeeService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(body, ct);
            return Results.Created($"/api/v1/employees/{created.Id}", created);
        }).WithValidation<EmployeeUpsert>();
        admin.MapPut("/{id:guid}", async (Guid id, EmployeeUpsert body, IEmployeeService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpdateAsync(id, body, ct))).WithValidation<EmployeeUpsert>();
    }
}

/// <summary>/usb-events, /file-events, /security-events, /audit-logs.</summary>
public static class EventListEndpoints
{
    public static void MapEventLists(this RouteGroupBuilder api)
    {
        var viewer = api.MapGroup("").RequireAuthorization(AuthDefaults.ViewerPolicy);

        viewer.MapGet("/usb-events", async ([AsParameters] UsbQuery q, IScopeResolver scopes, IEventQueryService events, TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(q.Device, q.Employee, q.Department, ct);
            return Results.Ok(await events.UsbAsync(new DeviceEventFilter(scope, TimeRange.Resolve(q.From, q.To, clock), q.Action), q.Paging, ct));
        });

        viewer.MapGet("/file-events", async ([AsParameters] FileQuery q, IScopeResolver scopes, IEventQueryService events, TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await scopes.ResolveAsync(q.Device, q.Employee, q.Department, ct);
            return Results.Ok(await events.FilesAsync(new DeviceEventFilter(scope, TimeRange.Resolve(q.From, q.To, clock), q.Action, q.Search), q.Paging, ct));
        });

        viewer.MapGet("/security-events", async ([AsParameters] SecurityEventsQuery q, ISecurityEventService security, CancellationToken ct) =>
            Results.Ok(await security.QueryAsync(new SecurityEventFilter(q.From, q.To, q.Device, q.Kind, q.Severity, q.Resolved), q.Paging, ct)));

        var admin = api.MapGroup("").RequireAuthorization(AuthDefaults.AdminPolicy);
        admin.MapPost("/security-events/{id:guid}/resolve", async (Guid id, ISecurityEventService security, CancellationToken ct) =>
            Results.Ok(await security.ResolveByIdAsync(id, ct)));

        admin.MapGet("/audit-logs", async ([AsParameters] AuditLogQuery q, IAuditQueryService audit, CancellationToken ct) =>
            Results.Ok(await audit.QueryAsync(q.From, q.To, q.User, q.Action, q.Paging, ct)));
    }
}

/// <summary>/reports/{kind} (daily, application-usage, active-idle, device-health, process-timeline, usb, agent-health, department-summary).</summary>
public static class ReportEndpoints
{
    public static void MapReports(this RouteGroupBuilder api)
    {
        api.MapGet("/reports/{kind}", async (string kind, [AsParameters] ReportHttpQuery q, IReportService reports, HttpContext http, CancellationToken ct) =>
        {
            var query = new ReportQuery(q.From, q.To, q.Date, q.Employee, q.Department, q.Device, q.TzOffsetMinutes, q.Application, q.Category);
            if (!string.Equals(q.Format, "csv", StringComparison.OrdinalIgnoreCase)) return Results.Ok(await reports.GenerateAsync(kind, query, ct));

            // Exporting is an ADMIN action (and is audited); viewing JSON is open to viewers.
            if (!http.User.IsInRole(Roles.Admin) && !http.User.IsInRole(Roles.SuperAdmin))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Exporting reports requires the ADMIN role.");
            var csv = await reports.ExportCsvAsync(kind, query, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"niss-{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        }).RequireAuthorization(AuthDefaults.ViewerPolicy);
    }
}

/// <summary>/settings/*: policies, application categories, retention, health thresholds (ADMIN+).</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/settings").RequireAuthorization(AuthDefaults.AdminPolicy);

        g.MapGet("/policies", async (IPolicyService policies, CancellationToken ct) => Results.Ok(await policies.ListAsync(ct)));
        g.MapPut("/policies/{id:guid}", async (Guid id, PolicyUpsert body, IPolicyService policies, CancellationToken ct) =>
            Results.Ok(await policies.UpsertAsync(id, body, ct))).WithValidation<PolicyUpsert>();
        // The dashboard creates scoped policies with PUT on the collection; POST is accepted as well.
        Delegate createPolicy = async (PolicyUpsert body, IPolicyService policies, CancellationToken ct) =>
        {
            var created = await policies.UpsertAsync(Guid.NewGuid(), body, ct);
            return Results.Created($"/api/v1/settings/policies/{created.Id}", created);
        };
        g.MapPut("/policies", createPolicy).WithValidation<PolicyUpsert>();
        g.MapPost("/policies", createPolicy).WithValidation<PolicyUpsert>();
        g.MapDelete("/policies/{id:guid}", async (Guid id, IPolicyService policies, CancellationToken ct) =>
        {
            await policies.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        g.MapGet("/app-categories", async (ICategoryService categories, CancellationToken ct) => Results.Ok(await categories.ListAsync(ct)));
        g.MapPut("/app-categories", async (List<AppCategoryRule> body, ICategoryService categories, CancellationToken ct) =>
            Results.Ok(await categories.ReplaceAsync(body, ct))).WithValidation<List<AppCategoryRule>>();

        g.MapGet("/retention", async (ISettingsAdminService settings, CancellationToken ct) => Results.Ok(await settings.GetRetentionAsync(ct)));
        g.MapPut("/retention", async (RetentionSettings body, ISettingsAdminService settings, CancellationToken ct) =>
            Results.Ok(await settings.UpdateRetentionAsync(body, ct))).WithValidation<RetentionSettings>();

        g.MapGet("/health-thresholds", async (ISettingsAdminService settings, CancellationToken ct) => Results.Ok(await settings.GetThresholdsAsync(ct)));
        g.MapPut("/health-thresholds", async (HealthThresholds body, ISettingsAdminService settings, CancellationToken ct) =>
            Results.Ok(await settings.UpdateThresholdsAsync(body, ct))).WithValidation<HealthThresholds>();
    }
}
