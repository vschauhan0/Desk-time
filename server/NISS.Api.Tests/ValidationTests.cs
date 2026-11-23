using System.Net;
using System.Text;
using System.Text.Json;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class ValidationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Enrollment_requests_are_validated()
    {
        using var client = factory.CreateClient();
        var errors = await ErrorsAsync(await client.PostAsync("/api/v1/devices/enroll", Json("{}")));
        Assert.Contains("enrollmentToken", errors.Keys);
        Assert.Contains("hostname", errors.Keys);
        Assert.Contains("windowsUsername", errors.Keys);

        var tooLong = TestKit.EnrollRequestFor("enr_x") with { Hostname = new string('h', 300) };
        Assert.Contains("hostname", (await ErrorsAsync(await client.PostJsonAsync("/api/v1/devices/enroll", tooLong))).Keys);
    }

    [Fact]
    public async Task Login_and_refresh_requests_are_validated()
    {
        using var client = factory.CreateClient();
        Assert.Contains("username", (await ErrorsAsync(await client.PostAsync("/api/v1/auth/login", Json("""{"username":"","password":""}""")))).Keys);
        Assert.Contains("refreshToken", (await ErrorsAsync(await client.PostAsync("/api/v1/auth/refresh", Json("""{"refreshToken":""}""")))).Keys);
    }

    [Fact]
    public async Task Malformed_or_empty_bodies_give_400_problem_details()
    {
        using var client = factory.CreateClient();
        var broken = await client.PostAsync("/api/v1/auth/login", Json("{ not json"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("application/problem+json", broken.Content.Headers.ContentType?.MediaType);

        var wrongType = await client.PostAsync("/api/v1/auth/login", Json("""{"username":123,"password":true}"""));
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        var empty = await client.PostAsync("/api/v1/auth/login", new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_and_status_requests_are_validated()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin);
        var errors = await ErrorsAsync(await d.Client.PostAsync("/api/v1/devices/heartbeat", Json("""{"deviceId":"","agentVersion":"","queueDepth":-1}""")));
        Assert.Contains("agentVersion", errors.Keys);
        Assert.Contains("queueDepth", errors.Keys);

        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.PostAsync("/api/v1/agent/status", Json("{}"))).StatusCode);
    }

    [Fact]
    public async Task Admin_user_requests_are_validated()
    {
        var admin = await factory.AdminAsync();
        var errors = await ErrorsAsync(await admin.PostJsonAsync("/api/v1/admin-users", new AdminUserUpsert("bad name!", "", "GOD", true, "short")));
        Assert.Contains("username", errors.Keys);
        Assert.Contains("displayName", errors.Keys);
        Assert.Contains("role", errors.Keys);
        Assert.Contains("password", errors.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/admin-users", new AdminUserUpsert("nopassword", "No Password", Roles.Viewer, true, null))).StatusCode);
    }

    [Fact]
    public async Task Employee_token_and_settings_requests_are_validated()
    {
        var admin = await factory.AdminAsync();
        Assert.Contains("email", (await ErrorsAsync(await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("E1", "Name", null, "not-an-email", true)))).Keys);
        Assert.Contains("employeeCode", (await ErrorsAsync(await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("", "Name", null, null, true)))).Keys);
        Assert.Contains("expiresInHours", (await ErrorsAsync(await admin.PostJsonAsync("/api/v1/enrollment-tokens", new EnrollmentTokenRequest(null, null, 0, null)))).Keys);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostJsonAsync("/api/v1/enrollment-tokens", new EnrollmentTokenRequest(null, "PC-9999", 24, null))).StatusCode);
    }

    [Fact]
    public async Task Bad_query_parameters_and_unknown_resources()
    {
        var admin = await factory.AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/devices/PC-1/activity?from=not-a-date")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/devices/PC-9999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/devices/PC-9999/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/employees/{Guid.NewGuid()}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/devices/PC-9999/disable", null)).StatusCode);

        // Paging is clamped instead of trusted.
        var page = await admin.GetJsonAsync<Paged<DeviceSummary>>("/api/v1/devices?page=-4&pageSize=100000");
        Assert.Equal(1, page.Page);
        Assert.Equal(500, page.PageSize);
    }

    [Fact]
    public async Task Problem_details_never_leak_internals()
    {
        var admin = await factory.AdminAsync();
        var response = await admin.GetAsync("/api/v1/devices/PC-9999");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("   at ", body);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cors_preflight_allows_the_headers_signalr_and_the_dashboard_send()
    {
        using var client = factory.CreateClient();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/hubs/monitor/negotiate");
        preflight.Headers.Add("Origin", "http://10.1.2.3:3000");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-requested-with,x-signalr-user-agent");
        var response = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        Assert.Contains("authorization", allowed);
        Assert.Contains("x-requested-with", allowed);
        Assert.Equal("http://10.1.2.3:3000", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        // Bearer-token requests from the dashboard origin work without cookies.
        var admin = await factory.AdminAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/overview");
        request.Headers.Add("Origin", "http://10.1.2.3:3000");
        var overview = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.True(overview.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
