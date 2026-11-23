using System.Net;
using System.Net.Http.Headers;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Device_secret_good_bad_and_missing()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await device.PostBatchAsync()).StatusCode);

        var bad = factory.CreateClient();
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("NissDevice", $"{device.DeviceId}:definitely-wrong");
        var rejected = await bad.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest(device.DeviceId, []));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Contains("NissDevice", rejected.Headers.WwwAuthenticate.ToString());

        var unknown = factory.CreateClient();
        unknown.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("NissDevice", "PC-999:whatever");
        Assert.Equal(HttpStatusCode.Unauthorized, (await unknown.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest("PC-999", []))).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest(device.DeviceId, []))).StatusCode);

        // A dashboard JWT is not an agent credential.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest(device.DeviceId, []))).StatusCode);
    }

    [Fact]
    public async Task Failed_device_authentication_raises_a_throttled_security_event()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin);
        var bad = factory.CreateClient();
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("NissDevice", $"{device.DeviceId}:wrong");
        for (var i = 0; i < 3; i++) await bad.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest(device.DeviceId, []));

        var events = await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={device.DeviceId}&kind=AUTH_FAILURE");
        Assert.Equal(1, events.Total);   // three failures, one event: throttled
    }

    [Fact]
    public async Task Rotated_secret_invalidates_the_old_one_immediately()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin);
        var old = device.Secret;

        var rotated = await (await device.Client.PostAsync("/api/v1/devices/credentials/rotate", null)).ReadAsync<RotateCredentialsResponse>();
        Assert.NotEqual(old, rotated.DeviceSecret);

        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostBatchAsync()).StatusCode);   // still using the old secret
        device.UseSecret(rotated.DeviceSecret);
        Assert.Equal(HttpStatusCode.OK, (await device.PostBatchAsync()).StatusCode);
    }

    [Fact]
    public async Task Admin_login_success_and_failure()
    {
        using var client = factory.CreateClient();
        var bad = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("admin", "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("nobody", "wrong-password"))).StatusCode);

        var (authed, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        Assert.Equal(Roles.SuperAdmin, auth.User.Role);
        Assert.Equal("admin", auth.User.Username);
        var me = await authed.GetJsonAsync<UserInfo>("/api/v1/auth/me");
        Assert.Equal(auth.User.Id, me.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_is_detected()
    {
        var (_, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        using var client = factory.CreateClient();

        var refreshed = await (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).ReadAsync<AuthResponse>();
        Assert.NotEqual(auth.RefreshToken, refreshed.RefreshToken);

        // The rotated-out token is dead, and presenting it again revokes the whole family.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshed.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Access_and_refresh_tokens_expire_with_the_clock()
    {
        var (client, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);   // 15 minute access token
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).StatusCode);

        var (_, second) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        factory.Clock.Advance(TimeSpan.FromHours(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken))).StatusCode);   // 8 hour refresh token
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        var superAdmin = await factory.AdminAsync();
        var admin = await factory.UserWithRoleAsync(superAdmin, Roles.Admin);
        var viewer = await factory.UserWithRoleAsync(superAdmin, Roles.Viewer);
        using var anonymous = factory.CreateClient();

        // Anonymous
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/overview")).StatusCode);

        // Viewer: read monitoring data, nothing else.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/settings/retention")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync("/api/v1/enrollment-tokens", new EnrollmentTokenRequest(null, null, 24, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("E1", "Name", null, null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/reports/usb?format=csv")).StatusCode);

        // Admin: everything except user management.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("E-ROLE", "Role Test", "IT", null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/admin-users")).StatusCode);

        // SUPERADMIN
        Assert.Equal(HttpStatusCode.OK, (await superAdmin.GetAsync("/api/v1/admin-users")).StatusCode);
    }

    [Fact]
    public async Task Admin_user_management_protects_the_last_superadmin_and_the_own_account()
    {
        var superAdmin = await factory.AdminAsync();
        var users = await superAdmin.GetJsonAsync<List<AdminUserDto>>("/api/v1/admin-users");
        var self = users.Single(u => u.Username == "admin");

        Assert.Equal(HttpStatusCode.Conflict, (await superAdmin.DeleteAsync($"/api/v1/admin-users/{self.Id}")).StatusCode);
        var demote = await superAdmin.PutJsonAsync($"/api/v1/admin-users/{self.Id}", new AdminUserUpsert("admin", "Administrator", Roles.Viewer, true, null));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var duplicate = await superAdmin.PostJsonAsync("/api/v1/admin-users", new AdminUserUpsert("ADMIN", "Dup", Roles.Viewer, true, "Another-Passw0rd!"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Health_is_anonymous_and_reports_database_connectivity()
    {
        using var client = factory.CreateClient();
        var health = await (await client.GetAsync("/api/v1/health")).ReadAsync<HealthResponse>();
        Assert.Equal("Healthy", health.Status);
        Assert.True(health.Database);
        Assert.False(string.IsNullOrEmpty(health.Version));
    }

    [Fact]
    public async Task Cors_allows_any_host_on_the_dashboard_port_only()
    {
        using var client = factory.CreateClient();
        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        allowed.Headers.Add("Origin", "http://192.168.1.50:3000");
        var ok = await client.SendAsync(allowed);
        Assert.Equal("http://192.168.1.50:3000", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", ok.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var denied = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        denied.Headers.Add("Origin", "http://evil.example:8080");
        Assert.False((await client.SendAsync(denied)).Headers.Contains("Access-Control-Allow-Origin"));
    }
}
