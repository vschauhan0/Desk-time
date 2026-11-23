using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Uses the production rate limits (login 5/min, enroll 10/min) on its own host.</summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(true, new Dictionary<string, string?>
        {
            ["RateLimits:LoginPerMinute"] = "5", ["RateLimits:EnrollPerMinute"] = "10", ["RateLimits:IngestPerMinute"] = "12",
        });
        await ((IAsyncLifetime)_factory).InitializeAsync();
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Login_is_limited_to_five_attempts_per_minute()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("admin", "nope-nope-nope"))).StatusCode);

        var blocked = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("admin", ApiFactory.AdminPassword));   // even the right password
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);

        // The window is one minute.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest("admin", ApiFactory.AdminPassword))).StatusCode);
    }

    [Fact]
    public async Task Token_refresh_is_not_throttled_like_password_login()
    {
        // Regression: the dashboard refreshes on every page load; the strict login limiter (5/min) locked users out after a few reloads.
        using var client = _factory.CreateClient();
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest("not-a-real-refresh-token"))).StatusCode);
    }

    [Fact]
    public async Task Enrollment_is_limited_to_ten_attempts_per_minute()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor("enr_guess-" + i))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostJsonAsync("/api/v1/devices/enroll", TestKit.EnrollRequestFor("enr_guess-11"))).StatusCode);
    }

    [Fact]
    public async Task Ingest_is_limited_per_device_and_devices_do_not_share_a_budget()
    {
        // (own host, so its admin login is not rate limited by the tests above: it has its own window as well)
        var (admin, _) = await _factory.LoginAsync("admin", ApiFactory.AdminPassword);
        var a = await _factory.EnrollDeviceAsync(admin);
        var b = await _factory.EnrollDeviceAsync(admin);

        for (var i = 0; i < 12; i++) Assert.Equal(HttpStatusCode.OK, (await a.PostBatchAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.PostBatchAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PostBatchAsync()).StatusCode);
    }
}

/// <summary>Data survives a host restart: events, devices, secrets, the generated JWT key and the seed data.</summary>
[Collection(ApiCollection.Name)]
public sealed class PersistenceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Events_are_persisted_with_utc_timestamps_and_raw_payload()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-PER");
        var at = new DateTimeOffset(2026, 5, 4, 14, 30, 15, 250, TimeSpan.FromHours(5.5));   // sent with a +05:30 offset
        var evt = d.Event(EventTypes.UsbConnected, at, P.Usb("Persisted Drive"));
        await d.IngestAsync(evt);

        Assert.Equal("2026-05-04 09:00:15.250", await TestDatabase.ScalarAsync<string>(
            "SELECT to_char(timestamp AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS') FROM event_ingestion WHERE event_id = @e", ("e", evt.EventId)));
        Assert.Equal("Persisted Drive", await TestDatabase.ScalarAsync<string>("SELECT payload->>'product' FROM event_ingestion WHERE event_id = @e", ("e", evt.EventId)));
        Assert.Equal(330L, await TestDatabase.ScalarAsync<long>("SELECT local_offset_minutes FROM event_ingestion WHERE event_id = @e", ("e", evt.EventId)));
        Assert.Equal("timestamp with time zone", await TestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'event_ingestion' AND column_name = 'timestamp'"));
        Assert.Equal("jsonb", await TestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'event_ingestion' AND column_name = 'payload'"));
    }

    [Fact]
    public async Task Schema_has_the_documented_tables_indexes_and_sequence()
    {
        string[] tables =
        [
            "employees", "devices", "device_users", "user_sessions", "process_events", "process_sessions", "application_sessions", "input_activity",
            "idle_sessions", "system_metrics", "usb_events", "file_events", "heartbeats", "agent_status", "security_events", "event_ingestion",
            "admin_users", "audit_logs", "configuration_policies", "enrollment_tokens", "refresh_tokens", "app_category_rules", "server_settings",
        ];
        foreach (var t in tables)
            Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t", ("t", t)));
        foreach (var t in new[] { "event_ingestion", "process_events", "input_activity", "application_sessions", "idle_sessions", "system_metrics", "usb_events", "file_events", "heartbeats" })
            Assert.True(await TestDatabase.ScalarAsync<long>(
                "SELECT count(*) FROM pg_indexes WHERE tablename = @t AND indexdef ILIKE '%(device_id, %'", ("t", t)) >= 1, $"{t} needs a (device_id, time) index");
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relkind = 'S' AND relname = 'device_number_seq'"));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE tablename = 'devices' AND indexdef ILIKE '%UNIQUE%(device_id)%'"));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE tablename = 'input_activity' AND indexdef ILIKE '%UNIQUE%(device_id, \"timestamp\")%'"));

        // Nothing that could hold typed text exists in the input table.
        Assert.Equal(0L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'input_activity' AND data_type IN ('text', 'character varying', 'bytea', 'jsonb')"));
    }

    [Fact]
    public async Task Everything_survives_a_restart_and_seeding_is_idempotent()
    {
        var (admin, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        var device = await factory.EnrollDeviceAsync(admin, "EMP-RESTART");
        var evt = device.Event(EventTypes.UsbConnected, factory.Clock.GetUtcNow(), P.Usb());
        await device.IngestAsync(evt);
        var rulesBefore = (await admin.GetJsonAsync<List<AppCategoryRule>>("/api/v1/settings/app-categories")).Count;
        var policiesBefore = (await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies")).Count;

        // A second host on the same database without resetting it (no JWT key in configuration: it must be read from the database).
        await using var second = new ApiFactory(false, null);
        await ((IAsyncLifetime)second).InitializeAsync();

        using var restartedAdmin = second.CreateClient();
        restartedAdmin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await restartedAdmin.GetAsync("/api/v1/auth/me")).StatusCode);   // the token issued before the restart is still valid

        var refreshed = await (await second.CreateClient().PostJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).ReadAsync<AuthResponse>();
        Assert.False(string.IsNullOrEmpty(refreshed.AccessToken));

        var restartedDevice = second.CreateClient();
        restartedDevice.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("NissDevice", $"{device.DeviceId}:{device.Secret}");
        var replay = await (await restartedDevice.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest(device.DeviceId, [evt]))).ReadAsync<EventBatchResponse>();
        Assert.Equal(1, replay.Duplicates);   // idempotency state is in the database, not in memory

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM admin_users"));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM configuration_policies WHERE scope = 'GLOBAL'"));
        Assert.Equal(rulesBefore, (int)await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM app_category_rules"));
        Assert.Equal(policiesBefore, (int)await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM configuration_policies"));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM server_settings WHERE key = 'jwt_signing_key'"));    }
}
