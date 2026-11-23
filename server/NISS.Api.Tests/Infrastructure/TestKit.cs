using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests.Infrastructure;

/// <summary>An enrolled test device with an HTTP client that authenticates as the agent.</summary>
public sealed class TestDevice(HttpClient client, string deviceId, string secret, string? employeeCode)
{
    public HttpClient Client { get; } = client;
    public string DeviceId { get; } = deviceId;
    public string Secret { get; private set; } = secret;
    public string? EmployeeCode { get; } = employeeCode;
    private long _sequence;

    public void UseSecret(string secret)
    {
        Secret = secret;
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("NissDevice", $"{DeviceId}:{secret}");
    }

    /// <summary>Builds an envelope with the next sequence number.</summary>
    public EventEnvelope Event(string type, DateTimeOffset at, object payload, long? sequence = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), 1, DeviceId, EmployeeCode, null, type, at, at.ToOffset(TimeSpan.FromHours(5.5)), EventSources.Session,
            sequence ?? Interlocked.Increment(ref _sequence), 0, ProtocolJson.ToElement(payload));

    public Task<HttpResponseMessage> PostBatchAsync(params EventEnvelope[] events) =>
        Client.PostAsJsonAsync("/api/v1/events/batch", new EventBatchRequest(DeviceId, events), ProtocolJson.Options);

    public async Task<EventBatchResponse> IngestAsync(params EventEnvelope[] events)
    {
        var response = await PostBatchAsync(events);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventBatchResponse>(ProtocolJson.Options))!;
    }

    public Task<HttpResponseMessage> HeartbeatAsync(HeartbeatRequest request) =>
        Client.PostAsJsonAsync("/api/v1/devices/heartbeat", request, ProtocolJson.Options);

    public HeartbeatRequest NewHeartbeat(DateTimeOffset now, LiveState? live = null, int queueDepth = 0, string version = "1.0.0",
        IReadOnlyList<SessionAgentInfo>? agents = null) =>
        new(DeviceId, version, now, 3600, "Running", agents ?? [new SessionAgentInfo(1, "alice", true, now)], queueDepth, null, now, null, 0, 1, live);
}

/// <summary>Request helpers used by all tests.</summary>
public static class TestKit
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        return JsonSerializer.Deserialize<T>(body, ProtocolJson.Options)!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ProtocolJson.Options);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, ProtocolJson.Options);

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url) => await (await client.GetAsync(url)).ReadAsync<T>();

    /// <summary>Logs in and returns a client that sends the bearer token.</summary>
    public static async Task<(HttpClient Client, AuthResponse Auth)> LoginAsync(this ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(username, password));
        var auth = await response.ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }

    public static async Task<HttpClient> AdminAsync(this ApiFactory factory) => (await factory.LoginAsync("admin", ApiFactory.AdminPassword)).Client;

    /// <summary>Creates a user with the given role through the SUPERADMIN API and returns a logged-in client.</summary>
    public static async Task<HttpClient> UserWithRoleAsync(this ApiFactory factory, HttpClient superAdmin, string role)
    {
        var username = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}"[..20];
        const string password = "Sup3r-Secret-Pass!";
        var created = await superAdmin.PostJsonAsync("/api/v1/admin-users", new AdminUserUpsert(username, username, role, true, password));
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        return (await factory.LoginAsync(username, password)).Client;
    }

    /// <summary>Creates an enrollment token through the API.</summary>
    public static async Task<EnrollmentTokenDto> CreateTokenAsync(this HttpClient admin, string? employeeCode = null, string? deviceId = null, int hours = 24) =>
        await (await admin.PostJsonAsync("/api/v1/enrollment-tokens", new EnrollmentTokenRequest(employeeCode, deviceId, hours, "test"))).ReadAsync<EnrollmentTokenDto>();

    public static EnrollRequest EnrollRequestFor(string token, string? machineGuid = null, string hostname = "DESK-TEST", string user = "alice") =>
        new(token, hostname, machineGuid ?? Guid.NewGuid().ToString(), "SN-" + Guid.NewGuid().ToString("N")[..6], null, user, "1.0.0", "Windows 11 Pro", "ACME", "Model X");

    /// <summary>Enrolls a new device (employee optional) and returns a device client.</summary>
    public static async Task<TestDevice> EnrollDeviceAsync(this ApiFactory factory, HttpClient admin, string? employeeCode = null, string? machineGuid = null, string? user = "alice")
    {
        var token = await admin.CreateTokenAsync(employeeCode);
        using var anonymous = factory.CreateClient();
        var enrolled = await (await anonymous.PostJsonAsync("/api/v1/devices/enroll", EnrollRequestFor(token.Token!, machineGuid, user: user ?? "alice"))).ReadAsync<EnrollResponse>();
        var client = factory.CreateClient();
        var device = new TestDevice(client, enrolled.DeviceId, enrolled.DeviceSecret, enrolled.EmployeeId);
        device.UseSecret(enrolled.DeviceSecret);
        return device;
    }

    public static Guid NewGuid() => Guid.NewGuid();
}
