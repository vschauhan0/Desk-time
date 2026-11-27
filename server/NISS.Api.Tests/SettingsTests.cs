using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class SettingsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Defaults_are_seeded()
    {
        var admin = await factory.AdminAsync();
        var policies = await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies");
        var global = Assert.Single(policies, p => p.Scope == "GLOBAL");
        Assert.Equal(30, global.Policy.HeartbeatSeconds);
        Assert.Equal(5, global.Policy.IdleThresholdMinutes);

        var retention = await admin.GetJsonAsync<RetentionSettings>("/api/v1/settings/retention");
        // Screenshot values come from ScreenshotStorage in serversettings.json: periodic 14 days, flagged 90.
        Assert.Equal(new RetentionSettings(90, 365, 180, 365, 14, 90), retention);
        var thresholds = await admin.GetJsonAsync<HealthThresholds>("/api/v1/settings/health-thresholds");
        Assert.Equal(new HealthThresholds(60, 300, 5000, null), thresholds);

        var rules = await admin.GetJsonAsync<List<AppCategoryRule>>("/api/v1/settings/app-categories");
        Assert.Contains(rules, r => r.Pattern == "chrome*" && r.Category == "Browser");
        Assert.Contains(rules, r => r.Pattern == "excel*" && r.Category == "Office");
        Assert.Contains(rules, r => r.Pattern == "code*" && r.Category == "Developer");
        Assert.Contains(rules, r => r.Pattern == "svchost" && r.Category == "System");
        Assert.Contains(rules, r => r.Pattern == "teams*" && r.Category == "Communication");
        Assert.Contains(rules, r => r.Pattern == "vlc*" && r.Category == "Media");
        Assert.Contains(rules, r => r.Pattern == "notepad*" && r.Category == "Utilities");

        Assert.Equal("admin", (await admin.GetJsonAsync<UserInfo>("/api/v1/auth/me")).Username);
    }

    [Fact]
    public async Task Policy_precedence_is_global_then_department_then_device()
    {
        var admin = await factory.AdminAsync();
        await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("EMP-POL", "Pol Icy", "Sales", null, true));
        var device = await factory.EnrollDeviceAsync(admin, "EMP-POL");

        async Task<AgentPolicy> Effective() => (await (await device.Client.GetAsync("/api/v1/agent/config")).ReadAsync<AgentConfigResponse>()).Policy;
        var baseline = await Effective();
        Assert.Equal(30, baseline.HeartbeatSeconds);

        // DEPARTMENT policy applies to the employee's devices (PUT on the collection creates, as the dashboard does).
        var created = await (await admin.PutJsonAsync("/api/v1/settings/policies",
            new PolicyUpsert("Sales", "DEPARTMENT", "Sales", new AgentPolicy { HeartbeatSeconds = 45, CaptureWindowTitles = false }))).ReadAsync<PolicyDto>();
        Assert.Equal("DEPARTMENT", created.Scope);
        var department = await Effective();
        Assert.Equal(45, department.HeartbeatSeconds);
        Assert.False(department.CaptureWindowTitles);
        Assert.True(department.Version > baseline.Version);   // agents notice the change through the config version

        // DEVICE policy wins over the department.
        var devicePolicy = await (await admin.PostJsonAsync("/api/v1/settings/policies",
            new PolicyUpsert("One PC", "DEVICE", device.DeviceId, new AgentPolicy { HeartbeatSeconds = 60 }))).ReadAsync<PolicyDto>();
        Assert.Equal(60, (await Effective()).HeartbeatSeconds);

        // Update by id, then delete: falls back to the department policy.
        var updated = await (await admin.PutJsonAsync($"/api/v1/settings/policies/{devicePolicy.Id}",
            new PolicyUpsert("One PC", "DEVICE", device.DeviceId, new AgentPolicy { HeartbeatSeconds = 90 }))).ReadAsync<PolicyDto>();
        Assert.Equal(90, updated.Policy.HeartbeatSeconds);
        Assert.Equal(90, (await Effective()).HeartbeatSeconds);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/settings/policies/{devicePolicy.Id}")).StatusCode);
        Assert.Equal(45, (await Effective()).HeartbeatSeconds);

        // Heartbeat responses advertise the new version so the agent re-reads its config.
        var hb = await (await device.HeartbeatAsync(device.NewHeartbeat(factory.Clock.GetUtcNow()))).ReadAsync<HeartbeatResponse>();
        Assert.Equal((await Effective()).Version, hb.ConfigVersion);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/settings/policies/{created.Id}")).StatusCode);
        Assert.Equal(30, (await Effective()).HeartbeatSeconds);
    }

    [Fact]
    public async Task Policy_rules_are_enforced()
    {
        var admin = await factory.AdminAsync();
        var global = (await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies")).Single(p => p.Scope == "GLOBAL");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJsonAsync("/api/v1/settings/policies", new PolicyUpsert("Second", "GLOBAL", null, new AgentPolicy()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/settings/policies/{global.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/settings/policies/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"/api/v1/settings/policies/{global.Id}", new PolicyUpsert("x", "GLOBAL", null, new AgentPolicy { HeartbeatSeconds = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/settings/policies", new PolicyUpsert("x", "DEVICE", null, new AgentPolicy()))).StatusCode);

        // Editing the global policy changes what every agent gets.
        var edited = await (await admin.PutJsonAsync($"/api/v1/settings/policies/{global.Id}",
            new PolicyUpsert("Default policy", "GLOBAL", null, global.Policy with { IdleThresholdMinutes = 10 }))).ReadAsync<PolicyDto>();
        Assert.Equal(10, edited.Policy.IdleThresholdMinutes);
        await admin.PutJsonAsync($"/api/v1/settings/policies/{global.Id}", new PolicyUpsert("Default policy", "GLOBAL", null, global.Policy));
    }

    [Fact]
    public async Task App_categories_can_be_replaced_and_drive_the_report_categories()
    {
        var admin = await factory.AdminAsync();
        var original = await admin.GetJsonAsync<List<AppCategoryRule>>("/api/v1/settings/app-categories");
        try
        {
            // Existing rules keep their ids, new ones are sent with an empty id (as the dashboard does), omitted ones are removed.
            var kept = original.Where(r => r.Pattern is "chrome*" or "excel*").ToList();
            var payload = kept.Concat([new AppCategoryRule(Guid.Empty, "legacyerp*", "Office", 200)]).ToList();
            var saved = await (await admin.PutJsonAsync("/api/v1/settings/app-categories", payload)).ReadAsync<List<AppCategoryRule>>();
            Assert.Equal(3, saved.Count);
            Assert.All(saved, r => Assert.NotEqual(Guid.Empty, r.Id));
            Assert.Equal(kept.Select(k => k.Id).Order(), saved.Where(r => r.Pattern != "legacyerp*").Select(r => r.Id).Order());

            var device = await factory.EnrollDeviceAsync(admin, "EMP-CAT");
            var at = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
            var app1 = Guid.NewGuid(); var app2 = Guid.NewGuid();
            await device.IngestAsync(
                device.Event(EventTypes.ApplicationBackground, at.AddMinutes(10), P.Background(app1, at, at.AddMinutes(10), 100, process: "LegacyERP.exe")),
                device.Event(EventTypes.ApplicationBackground, at.AddMinutes(20), P.Background(app2, at.AddMinutes(10), at.AddMinutes(20), 100, process: "code.exe")));   // no longer matches "code*"

            var apps = await admin.GetJsonAsync<ApplicationActivityResponse>(
                $"/api/v1/devices/{device.DeviceId}/applications?from={Uri.EscapeDataString(at.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(at.AddHours(2).ToString("o"))}");
            Assert.Equal("Office", apps.Totals.Single(t => t.ProcessName == "LegacyERP").Category);
            Assert.Equal("Unknown", apps.Totals.Single(t => t.ProcessName == "code").Category);
            Assert.Equal("Office", apps.Sessions.Items.Single(s => s.ProcessName == "LegacyERP.exe").Category);
        }
        finally
        {
            await admin.PutJsonAsync("/api/v1/settings/app-categories", original);
        }
    }

    [Fact]
    public async Task Retention_and_thresholds_are_editable_and_validated()
    {
        var admin = await factory.AdminAsync();
        var saved = new RetentionSettings(30, 60, 90, 0, 7, 45);
        var retention = await (await admin.PutJsonAsync("/api/v1/settings/retention", saved)).ReadAsync<RetentionSettings>();
        Assert.Equal(saved, await admin.GetJsonAsync<RetentionSettings>("/api/v1/settings/retention"));
        Assert.Equal(retention, saved);
        await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(90, 365, 180, 365, 14, 90));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(-1, 1, 1, 1))).StatusCode);
        // Screenshot retention is range-checked too, when supplied.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(1, 1, 1, 1, -1, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(1, 1, 1, 1, 1, 99_999))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/settings/health-thresholds", new HealthThresholds(100, 50, 5000, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/settings/health-thresholds", new HealthThresholds(60, 300, 5000, "not-a-version"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync("/api/v1/settings/app-categories", new List<AppCategoryRule> { new(Guid.Empty, "x*", "Bogus", 1) })).StatusCode);
    }

    [Fact]
    public async Task Employees_can_be_created_updated_and_searched()
    {
        var admin = await factory.AdminAsync();
        var created = await (await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("EMP-CRUD", "Casey Crud", "Legal", "casey@example.test", true))).ReadAsync<EmployeeDto>();
        Assert.Equal(0, created.DeviceCount);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("emp-crud", "Dup", null, null, true))).StatusCode);

        var updated = await (await admin.PutJsonAsync($"/api/v1/employees/{created.Id}", new EmployeeUpsert("EMP-CRUD", "Casey Crud-Smith", "Compliance", null, false))).ReadAsync<EmployeeDto>();
        Assert.Equal(("Casey Crud-Smith", "Compliance", false), (updated.FullName, updated.Department, updated.Active));

        var found = await admin.GetJsonAsync<Paged<EmployeeDto>>("/api/v1/employees?search=crud-smith&department=compliance");
        Assert.Equal(created.Id, found.Items.Single().Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutJsonAsync($"/api/v1/employees/{Guid.NewGuid()}", new EmployeeUpsert("X", "X", null, null, true))).StatusCode);
    }
}
