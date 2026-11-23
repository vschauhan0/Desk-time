using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuditTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<List<AuditLogDto>> LogsAsync(HttpClient admin, string query = "") =>
        (await admin.GetJsonAsync<Paged<AuditLogDto>>("/api/v1/audit-logs?pageSize=500" + query)).Items.ToList();

    [Fact]
    public async Task Every_admin_action_is_written_to_the_audit_log()
    {
        using var anonymous = factory.CreateClient();
        await anonymous.PostJsonAsync("/api/v1/auth/login", new LoginRequest("admin", "wrong-password-here"));   // failed login

        var (admin, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        var token = await admin.CreateTokenAsync("EMP-AUD");
        using (var device = factory.CreateClient())
            await device.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!));
        var d = await factory.EnrollDeviceAsync(admin, "EMP-AUD");
        var employee = await (await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("EMP-AUD2", "Audit Person", null, null, true))).ReadAsync<EmployeeDto>();
        await admin.PutJsonAsync($"/api/v1/employees/{employee.Id}", new EmployeeUpsert("EMP-AUD2", "Audit Person 2", "QA", null, true));
        await admin.PutJsonAsync("/api/v1/settings/retention", new RetentionSettings(90, 365, 180, 365));
        var global = (await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies")).Single(p => p.Scope == "GLOBAL");
        await admin.PutJsonAsync($"/api/v1/settings/policies/{global.Id}", new PolicyUpsert(global.Name, "GLOBAL", null, global.Policy));
        await admin.PutJsonAsync("/api/v1/settings/app-categories", await admin.GetJsonAsync<List<AppCategoryRule>>("/api/v1/settings/app-categories"));
        await admin.PostAsync($"/api/v1/devices/{d.DeviceId}/disable", null);
        await admin.PostAsync($"/api/v1/devices/{d.DeviceId}/enable", null);
        await admin.PostAsync($"/api/v1/devices/{d.DeviceId}/re-enroll", null);
        await admin.PostAsync($"/api/v1/devices/{d.DeviceId}/revoke", null);
        await admin.GetAsync($"/api/v1/devices/{d.DeviceId}/diagnostics/download");
        await admin.GetAsync("/api/v1/reports/usb?format=csv");
        await admin.PostJsonAsync("/api/v1/admin-users", new AdminUserUpsert("audit-viewer", "Audit Viewer", Roles.Viewer, true, "Audit-Viewer-Passw0rd!"));
        await admin.DeleteAsync($"/api/v1/enrollment-tokens/{token.Id}");
        await admin.PostJsonAsync("/api/v1/auth/logout", new RefreshRequest(auth.RefreshToken));

        var actions = (await LogsAsync(admin)).Select(l => l.Action).ToHashSet();
        string[] expected =
        [
            "LOGIN", "LOGIN_FAILED", "LOGOUT", "ENROLLMENT_TOKEN_CREATED", "ENROLLMENT_TOKEN_DELETED", "DEVICE_ENROLLED",
            "EMPLOYEE_CREATED", "EMPLOYEE_UPDATED", "SETTINGS_UPDATED", "POLICY_UPDATED", "APP_CATEGORIES_UPDATED",
            "DEVICE_DISABLED", "DEVICE_ENABLED", "DEVICE_REENROLL_REQUESTED", "DEVICE_REVOKED", "DIAGNOSTICS_DOWNLOADED",
            "REPORT_EXPORTED", "ADMIN_USER_CREATED",
        ];
        foreach (var action in expected) Assert.Contains(action, actions);
    }

    [Fact]
    public async Task Audit_entries_carry_actor_target_and_never_secrets()
    {
        var admin = await factory.AdminAsync();
        var token = await admin.CreateTokenAsync("EMP-AUD3");
        await admin.PostAsync("/api/v1/devices/PC-000/disable", null);   // failing actions are not logged as success
        var logs = await LogsAsync(admin, "&action=ENROLLMENT_TOKEN_CREATED");

        var entry = logs.First(l => l.Target == "EMP-AUD3");
        Assert.Equal("admin", entry.Username);
        Assert.DoesNotContain(token.Token!, string.Join(' ', logs.Select(l => $"{l.Target} {l.Detail}")));

        var failed = await LogsAsync(admin, "&action=LOGIN_FAILED");
        Assert.All(failed, l => Assert.DoesNotContain("password", (l.Detail ?? "").Replace("bad credentials", ""), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(await LogsAsync(admin, "&action=DEVICE_DISABLED"), l => l.Target == "PC-000");
    }

    [Fact]
    public async Task Audit_log_is_filterable_and_restricted_to_admins()
    {
        var superAdmin = await factory.AdminAsync();
        var viewer = await factory.UserWithRoleAsync(superAdmin, Roles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/audit-logs")).StatusCode);

        var byUser = await LogsAsync(superAdmin, "&user=admin&action=ADMIN_USER_CREATED");
        Assert.NotEmpty(byUser);
        Assert.All(byUser, l => Assert.Equal("ADMIN_USER_CREATED", l.Action));

        var future = Uri.EscapeDataString(factory.Clock.GetUtcNow().AddDays(1).ToString("o"));
        Assert.Empty(await LogsAsync(superAdmin, $"&from={future}"));
    }
}
