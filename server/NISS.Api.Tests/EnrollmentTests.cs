using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class EnrollmentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Enrollment_creates_device_employee_and_returns_secret_once()
    {
        var admin = await factory.AdminAsync();
        var token = await admin.CreateTokenAsync("EMP7001");
        Assert.StartsWith("enr_", token.Token);

        using var anonymous = factory.CreateClient();
        var response = await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!, user: "bob"));
        var enrolled = await response.ReadAsync<EnrollResponse>();

        Assert.Matches(@"^PC-\d{3,}$", enrolled.DeviceId);
        Assert.Equal("EMP7001", enrolled.EmployeeId);
        Assert.False(string.IsNullOrWhiteSpace(enrolled.DeviceSecret));
        Assert.Equal(1, enrolled.Policy.Version);

        // Only the SHA-256 hash of the secret is stored; the placeholder employee and the windows user mapping exist.
        var stored = await TestDatabase.ScalarAsync<string>("SELECT encode(secret_hash, 'hex') FROM devices WHERE device_id = @d", ("d", enrolled.DeviceId));
        Assert.Equal(64, stored.Length);
        Assert.DoesNotContain(enrolled.DeviceSecret, stored);
        Assert.Equal("EMP7001", await TestDatabase.ScalarAsync<string>("SELECT full_name FROM employees WHERE employee_code = 'EMP7001'"));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM device_users du JOIN devices d ON d.id = du.device_id WHERE d.device_id = @d AND du.windows_username = 'bob' AND du.employee_id IS NOT NULL", ("d", enrolled.DeviceId)));

        // The token listing never reveals the token again and shows who used it.
        var tokens = await admin.GetJsonAsync<List<EnrollmentTokenDto>>("/api/v1/enrollment-tokens");
        var listed = Assert.Single(tokens, t => t.Id == token.Id);
        Assert.Null(listed.Token);
        Assert.Equal(enrolled.DeviceId, listed.UsedByDeviceId);
        Assert.NotNull(listed.UsedAtUtc);
    }

    [Fact]
    public async Task Token_is_single_use()
    {
        var admin = await factory.AdminAsync();
        var token = await admin.CreateTokenAsync();
        using var anonymous = factory.CreateClient();

        var first = await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Concurrent_use_of_one_token_enrolls_exactly_one_device()
    {
        var admin = await factory.AdminAsync();
        var token = await admin.CreateTokenAsync();
        using var anonymous = factory.CreateClient();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!))));
        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, attempts.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Unknown_and_expired_tokens_are_refused()
    {
        var admin = await factory.AdminAsync();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor("enr_not-a-real-token"))).StatusCode);

        var token = await admin.CreateTokenAsync(hours: 1);
        factory.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!))).StatusCode);
    }

    [Fact]
    public async Task Same_machine_guid_reenrolls_the_existing_device_and_rotates_the_secret()
    {
        var admin = await factory.AdminAsync();
        var machine = Guid.NewGuid().ToString();
        var first = await factory.EnrollDeviceAsync(admin, "EMP7100", machine);

        var token = await admin.CreateTokenAsync();
        using var anonymous = factory.CreateClient();
        var again = await (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!, machine))).ReadAsync<EnrollResponse>();

        Assert.Equal(first.DeviceId, again.DeviceId);
        Assert.NotEqual(first.Secret, again.DeviceSecret);
        // The old secret stops working immediately; the new one works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.PostBatchAsync()).StatusCode);
        first.UseSecret(again.DeviceSecret);
        Assert.Equal(HttpStatusCode.OK, (await first.PostBatchAsync()).StatusCode);
    }

    [Fact]
    public async Task Device_ids_come_from_a_sequence()
    {
        var admin = await factory.AdminAsync();
        var a = await factory.EnrollDeviceAsync(admin);
        var b = await factory.EnrollDeviceAsync(admin);
        var na = int.Parse(a.DeviceId[3..]);
        var nb = int.Parse(b.DeviceId[3..]);
        Assert.Equal(na + 1, nb);
    }

    [Fact]
    public async Task Revoked_device_is_blocked_and_can_only_return_with_a_bound_reenroll_token()
    {
        var admin = await factory.AdminAsync();
        var machine = Guid.NewGuid().ToString();
        var device = await factory.EnrollDeviceAsync(admin, "EMP7200", machine);
        Assert.Equal(HttpStatusCode.OK, (await device.PostBatchAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostBatchAsync()).StatusCode);

        var heartbeat = await (await device.HeartbeatAsync(device.NewHeartbeat(factory.Clock.GetUtcNow()))).ReadAsync<HeartbeatResponse>();
        Assert.Equal(DeviceHealth.Revoked, heartbeat.DeviceStatus);

        // A generic token with the same machine guid must not resurrect a revoked device.
        var generic = await admin.CreateTokenAsync();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(generic.Token!, machine))).StatusCode);

        // The admin re-enroll action issues a token bound to this device.
        var bound = await (await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/re-enroll", null)).ReadAsync<EnrollmentTokenDto>();
        Assert.Equal(device.DeviceId, bound.DeviceId);
        var back = await (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(bound.Token!, Guid.NewGuid().ToString()))).ReadAsync<EnrollResponse>();
        Assert.Equal(device.DeviceId, back.DeviceId);
        device.UseSecret(back.DeviceSecret);
        Assert.Equal(HttpStatusCode.OK, (await device.PostBatchAsync()).StatusCode);
    }

    [Fact]
    public async Task Disable_and_enable_are_reflected_on_ingest_and_heartbeat()
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin);

        await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/disable", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostBatchAsync()).StatusCode);
        var hb = await (await device.HeartbeatAsync(device.NewHeartbeat(factory.Clock.GetUtcNow()))).ReadAsync<HeartbeatResponse>();
        Assert.Equal(DeviceHealth.Disabled, hb.DeviceStatus);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostBatchAsync()).StatusCode);
        hb = await (await device.HeartbeatAsync(device.NewHeartbeat(factory.Clock.GetUtcNow()))).ReadAsync<HeartbeatResponse>();
        Assert.Equal("ACTIVE", hb.DeviceStatus);
    }

    [Fact]
    public async Task Enrollment_token_can_be_bound_to_an_employee_and_deleted()
    {
        var admin = await factory.AdminAsync();
        var token = await admin.CreateTokenAsync("EMP7300");
        Assert.Equal("EMP7300", token.EmployeeCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/enrollment-tokens/{token.Id}")).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor(token.Token!))).StatusCode);
    }
}
