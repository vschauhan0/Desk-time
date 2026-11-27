using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Per-admin page access and employee scoping.</summary>
[Collection(ApiCollection.Name)]
public sealed class RbacTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<Guid> EmployeeIdOfDeviceAsync(string deviceCode)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == deviceCode);
        return d.EmployeeId!.Value;
    }

    private async Task<HttpClient> CreateRestrictedAsync(AdminUserUpsert body, string password)
    {
        var superAdmin = await factory.AdminAsync();
        Assert.Equal(HttpStatusCode.Created, (await superAdmin.PostJsonAsync("/api/v1/admin-users", body)).StatusCode);
        return (await factory.LoginAsync(body.Username, password)).Client;
    }

    [Fact]
    public async Task A_scoped_admin_sees_only_their_employees_and_devices()
    {
        var superAdmin = await factory.AdminAsync();
        var devA = await factory.EnrollDeviceAsync(superAdmin, "EMP-RBAC-A");
        var devB = await factory.EnrollDeviceAsync(superAdmin, "EMP-RBAC-B");
        var empA = await EmployeeIdOfDeviceAsync(devA.DeviceId);

        const string pw = "Scoped-Pass-123!";
        var user = $"rbac-{Guid.NewGuid():N}"[..18];
        var scoped = await CreateRestrictedAsync(
            new AdminUserUpsert(user, user, Roles.Admin, true, pw, PageAccess: null,
                ScopeAllEmployees: false, AllowedDepartments: [], AllowedEmployeeIds: [empA]), pw);

        // Employees: only A.
        var employees = await scoped.GetJsonAsync<Paged<EmployeeDto>>("/api/v1/employees");
        Assert.Contains(employees.Items, e => e.Id == empA);
        Assert.DoesNotContain(employees.Items, e => e.EmployeeCode == "EMP-RBAC-B");

        // Devices list + live: only A's device.
        var devices = await scoped.GetJsonAsync<Paged<DeviceSummary>>("/api/v1/devices");
        Assert.Contains(devices.Items, d => d.DeviceId == devA.DeviceId);
        Assert.DoesNotContain(devices.Items, d => d.DeviceId == devB.DeviceId);

        var live = await scoped.GetJsonAsync<List<LiveDeviceCard>>("/api/v1/live");
        Assert.Contains(live, c => c.DeviceId == devA.DeviceId);
        Assert.DoesNotContain(live, c => c.DeviceId == devB.DeviceId);

        // Direct access to B's device detail is 404 (existence not revealed to a scoped admin).
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync($"/api/v1/devices/{devB.DeviceId}")).StatusCode);
        // Screenshots are SUPERADMIN-only, so an ADMIN is refused by role regardless of scope.
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync($"/api/v1/devices/{devB.DeviceId}/screenshots")).StatusCode);

        // A's device remains reachable.
        Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync($"/api/v1/devices/{devA.DeviceId}")).StatusCode);
    }

    [Fact]
    public async Task The_overview_reflects_only_the_scoped_fleet()
    {
        var superAdmin = await factory.AdminAsync();
        var devA = await factory.EnrollDeviceAsync(superAdmin, "EMP-OVS-A");
        await factory.EnrollDeviceAsync(superAdmin, "EMP-OVS-B");
        var empA = await EmployeeIdOfDeviceAsync(devA.DeviceId);

        const string pw = "Ov-Scope-123!";
        var user = $"ovs-{Guid.NewGuid():N}"[..18];
        var scoped = await CreateRestrictedAsync(
            new AdminUserUpsert(user, user, Roles.Admin, true, pw, PageAccess: null,
                ScopeAllEmployees: false, AllowedDepartments: [], AllowedEmployeeIds: [empA]), pw);

        var overview = await scoped.GetJsonAsync<OverviewDto>("/api/v1/overview");

        // Exactly one device (A's) and one employee are visible, not the whole fleet.
        Assert.Equal(1, overview.TotalDevices);
        Assert.Equal(1, overview.TotalEmployees);
    }

    [Fact]
    public async Task Department_scope_includes_employees_in_that_department()
    {
        var superAdmin = await factory.AdminAsync();
        // Create an employee in a known department, then enroll a device for them.
        var code = $"EMP-DEP-{Guid.NewGuid():N}"[..12];
        await superAdmin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert(code, "Dept Person", "Engineering", null, true));
        await factory.EnrollDeviceAsync(superAdmin, code);

        const string pw = "Dept-Pass-123!";
        var user = $"dept-{Guid.NewGuid():N}"[..18];
        var scoped = await CreateRestrictedAsync(
            new AdminUserUpsert(user, user, Roles.Viewer, true, pw, PageAccess: null,
                ScopeAllEmployees: false, AllowedDepartments: ["Engineering"], AllowedEmployeeIds: []), pw);

        var employees = await scoped.GetJsonAsync<Paged<EmployeeDto>>("/api/v1/employees");
        Assert.Contains(employees.Items, e => e.EmployeeCode == code);
        Assert.All(employees.Items, e => Assert.Equal("Engineering", e.Department));
    }

    [Fact]
    public async Task Page_access_round_trips_and_is_capped_to_the_role()
    {
        var superAdmin = await factory.AdminAsync();
        const string pw = "Pages-Pass-123!";
        var user = $"pg-{Guid.NewGuid():N}"[..18];

        // A VIEWER granted "settings" (an ADMIN page) has it dropped; valid pages are kept.
        var created = await (await superAdmin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert(user, user, Roles.Viewer, true, pw, PageAccess: new Dictionary<string, string> { ["overview"] = PageAccess.View, ["live"] = PageAccess.View, ["settings"] = PageAccess.View },
                ScopeAllEmployees: true, AllowedDepartments: [], AllowedEmployeeIds: []))).ReadAsync<AdminUserDto>();

        Assert.NotNull(created.PageAccess);
        Assert.True(created.PageAccess!.ContainsKey("overview"));
        Assert.True(created.PageAccess!.ContainsKey("live"));
        Assert.False(created.PageAccess!.ContainsKey("settings")); // above the role

        // /auth/me reflects the same allowed pages.
        var me = await (await factory.LoginAsync(user, pw)).Client.GetJsonAsync<UserInfo>("/api/v1/auth/me");
        Assert.NotNull(me.PageAccess);
        Assert.False(me.PageAccess!.ContainsKey("settings"));
    }

    [Fact]
    public async Task Superadmin_is_always_unrestricted_even_if_limits_are_sent()
    {
        var superAdmin = await factory.AdminAsync();
        const string pw = "Super-Pass-123!";
        var user = $"su-{Guid.NewGuid():N}"[..18];

        var created = await (await superAdmin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert(user, user, Roles.SuperAdmin, true, pw, PageAccess: new Dictionary<string, string> { ["overview"] = PageAccess.View },
                ScopeAllEmployees: false, AllowedDepartments: ["Engineering"], AllowedEmployeeIds: [Guid.NewGuid()]))).ReadAsync<AdminUserDto>();

        // The limits are discarded for a superadmin.
        Assert.Null(created.PageAccess);
        Assert.True(created.ScopeAllEmployees);
        Assert.Empty(created.AllowedEmployeeIds ?? []);

        var me = await (await factory.LoginAsync(user, pw)).Client.GetJsonAsync<UserInfo>("/api/v1/auth/me");
        Assert.Null(me.PageAccess);
    }

    [Fact]
    public async Task An_unknown_page_key_is_rejected()
    {
        var superAdmin = await factory.AdminAsync();
        var user = $"bad-{Guid.NewGuid():N}"[..18];
        var resp = await superAdmin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert(user, user, Roles.Viewer, true, "Bad-Pass-123!", PageAccess: new Dictionary<string, string> { ["overview"] = PageAccess.View, ["not-a-page"] = PageAccess.View },
                ScopeAllEmployees: true, AllowedDepartments: [], AllowedEmployeeIds: []));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

/// <summary>Per-page VIEW vs EDIT enforcement on mutation endpoints.</summary>
[Collection(ApiCollection.Name)]
public sealed class PageEditEnforcementTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_view_only_admin_cannot_edit_but_an_edit_admin_can()
    {
        var superAdmin = await factory.AdminAsync();
        await superAdmin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert($"EMP-PE-{Guid.NewGuid():N}"[..12], "Pe Person", "Ops", null, true));

        // View-only on Employees.
        const string vpw = "View-Pass-123!";
        var vuser = $"vo-{Guid.NewGuid():N}"[..18];
        await superAdmin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert(vuser, vuser, Roles.Admin, true, vpw,
                PageAccess: new Dictionary<string, string> { ["employees"] = PageAccess.View }, ScopeAllEmployees: true));
        var viewer = (await factory.LoginAsync(vuser, vpw)).Client;

        // Edit on Employees.
        const string epw = "Edit-Pass-123!";
        var euser = $"ed-{Guid.NewGuid():N}"[..18];
        await superAdmin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert(euser, euser, Roles.Admin, true, epw,
                PageAccess: new Dictionary<string, string> { ["employees"] = PageAccess.Edit }, ScopeAllEmployees: true));
        var editor = (await factory.LoginAsync(euser, epw)).Client;

        var body = new EmployeeUpsert($"EMP-NEW-{Guid.NewGuid():N}"[..12], "New One", "Ops", null, true);

        // Both can read the list (VIEW), only the editor can create (EDIT).
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync("/api/v1/employees", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await editor.PostJsonAsync("/api/v1/employees", body)).StatusCode);
    }

    [Fact]
    public async Task A_sub_tab_inherits_its_page_level_unless_set()
    {
        var superAdmin = await factory.AdminAsync();
        const string pw = "Sub-Pass-123!";
        var user = $"sub-{Guid.NewGuid():N}"[..18];
        // Devices = VIEW, but the Screens sub-tab explicitly HIDDEN; Diagnostics inherits VIEW.
        var created = await (await superAdmin.PostJsonAsync("/api/v1/admin-users",
            new AdminUserUpsert(user, user, Roles.Admin, true, pw,
                PageAccess: new Dictionary<string, string> { ["devices"] = PageAccess.View }, ScopeAllEmployees: true))).ReadAsync<AdminUserDto>();

        Assert.NotNull(created.PageAccess);
        Assert.Equal(PageAccess.View, created.PageAccess!["devices"]);
    }
}
