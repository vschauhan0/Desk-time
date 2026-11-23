using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class IngestionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private static Task<long> Count(string table, TestDevice d) => TestDatabase.ScalarAsync<long>(
        $"SELECT count(*) FROM {table} t JOIN devices dev ON dev.id = t.device_id WHERE dev.device_id = @d", ("d", d.DeviceId));

    private async Task<(HttpClient Admin, TestDevice Device)> NewDeviceAsync(string? employee = "EMP-ING")
    {
        var admin = await factory.AdminAsync();
        return (admin, await factory.EnrollDeviceAsync(admin, employee));
    }

    [Fact]
    public async Task Duplicate_batch_is_reported_as_DUPLICATE_and_changes_nothing()
    {
        var (_, d) = await NewDeviceAsync();
        var appId = Guid.NewGuid();
        var batch = new[]
        {
            d.Event(EventTypes.UserLogin, T0, P.Session()),
            d.Event(EventTypes.ApplicationForeground, T0.AddSeconds(5), P.Foreground(appId)),
            d.Event(EventTypes.UsbConnected, T0.AddMinutes(1), P.Usb()),
        };

        var first = await d.IngestAsync(batch);
        Assert.Equal((3, 0, 0), (first.Accepted, first.Duplicates, first.Rejected));
        var before = (await Count("event_ingestion", d), await Count("user_sessions", d), await Count("application_sessions", d), await Count("usb_events", d));

        var second = await d.IngestAsync(batch);
        Assert.Equal((0, 3, 0), (second.Accepted, second.Duplicates, second.Rejected));
        Assert.All(second.Results, r => Assert.Equal(EventResultStatus.Duplicate, r.Status));
        Assert.Equal(before, (await Count("event_ingestion", d), await Count("user_sessions", d), await Count("application_sessions", d), await Count("usb_events", d)));
        Assert.Equal((3L, 1L, 1L, 1L), before);
    }

    [Fact]
    public async Task Same_event_twice_inside_one_batch_is_accepted_once()
    {
        var (_, d) = await NewDeviceAsync();
        var e = d.Event(EventTypes.UsbConnected, T0, P.Usb());
        var result = await d.IngestAsync(e, e);
        Assert.Equal((1, 1, 0), (result.Accepted, result.Duplicates, result.Rejected));
        Assert.Equal(1L, await Count("usb_events", d));
    }

    [Fact]
    public async Task Malformed_events_are_rejected_individually_and_do_not_fail_the_batch()
    {
        var (_, d) = await NewDeviceAsync();
        var good = d.Event(EventTypes.UsbConnected, T0, P.Usb());
        var unknownType = d.Event("NOT_A_REAL_EVENT", T0, new { });
        var wrongSchema = good with { EventId = Guid.NewGuid(), SchemaVersion = 2 };
        var wrongDevice = good with { EventId = Guid.NewGuid(), DeviceId = "PC-999" };
        var noEventId = good with { EventId = Guid.Empty };
        var badPayload = d.Event(EventTypes.ApplicationForeground, T0, new { appSessionId = Guid.Empty, processName = "" });
        var typeMismatch = d.Event(EventTypes.MouseActivity, T0, new { bucketStartUtc = "not a date" });
        var noTimestamp = good with { EventId = Guid.NewGuid(), TimestampUtc = default };

        var result = await d.IngestAsync(unknownType, wrongSchema, good, wrongDevice, noEventId, badPayload, typeMismatch, noTimestamp);

        Assert.Equal((1, 0, 7), (result.Accepted, result.Duplicates, result.Rejected));
        Assert.Equal(EventResultStatus.Accepted, result.Results[2].Status);
        Assert.Equal(good.EventId, result.Results[2].EventId);
        Assert.All(result.Results.Where((_, i) => i != 2), r => { Assert.Equal(EventResultStatus.Rejected, r.Status); Assert.False(string.IsNullOrEmpty(r.Error)); });
        Assert.Equal(1L, await Count("event_ingestion", d));   // rejected events are not stored
    }

    [Fact]
    public async Task Batch_level_validation_returns_400()
    {
        var (_, d) = await NewDeviceAsync();
        var tooMany = Enumerable.Range(0, 501).Select(i => d.Event(EventTypes.UsbConnected, T0, P.Usb())).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await d.PostBatchAsync(tooMany)).StatusCode);

        var max = Enumerable.Range(0, 500).Select(i => d.Event(EventTypes.NetworkConnected, T0.AddSeconds(i), new NetworkPayload(true, ["10.0.0.1"], "Ethernet"))).ToArray();
        var ok = await d.IngestAsync(max);
        Assert.Equal(500, ok.Accepted);

        var mismatch = await d.Client.PostJsonAsync("/api/v1/events/batch", new EventBatchRequest("PC-999", []));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var noEvents = await d.Client.PostAsync("/api/v1/events/batch", new StringContent("{\"deviceId\":\"" + d.DeviceId + "\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noEvents.StatusCode);
    }

    [Fact]
    public async Task Metrics_endpoint_accepts_only_system_metric_events()
    {
        var (_, d) = await NewDeviceAsync();
        var metric = d.Event(EventTypes.SystemMetric, T0, P.Metric(cpu: 42, mem: 71));
        var other = d.Event(EventTypes.UsbConnected, T0, P.Usb());

        var response = await d.Client.PostJsonAsync("/api/v1/metrics/batch", new EventBatchRequest(d.DeviceId, [metric, other]));
        var body = await response.ReadAsync<EventBatchResponse>();
        Assert.Equal((1, 0, 1), (body.Accepted, body.Duplicates, body.Rejected));
        Assert.Equal(EventResultStatus.Rejected, body.Results[1].Status);
        Assert.Equal(HttpStatusCode.OK, (await d.Client.PostJsonAsync("/api/v1/metrics", new EventBatchRequest(d.DeviceId, []))).StatusCode);   // alias

        Assert.Equal(42d, await TestDatabase.ScalarAsync<double>("SELECT cpu_percent FROM system_metrics m JOIN devices d ON d.id = m.device_id WHERE d.device_id = @d", ("d", d.DeviceId)));
        Assert.Equal(1L, await Count("system_metrics", d));
    }

    [Fact]
    public async Task Events_are_processed_in_sequence_order_regardless_of_array_order()
    {
        var (_, d) = await NewDeviceAsync();
        var appId = Guid.NewGuid();
        var start = T0; var end = T0.AddMinutes(10);
        var fg = d.Event(EventTypes.ApplicationForeground, start, P.Foreground(appId), sequence: 10);
        var bg = d.Event(EventTypes.ApplicationBackground, end, P.Background(appId, start, end, 400), sequence: 11);

        var result = await d.IngestAsync(bg, fg);   // arrives reversed
        Assert.Equal(2, result.Accepted);
        Assert.Equal(bg.EventId, result.Results[0].EventId);   // results keep request order
        Assert.Equal(fg.EventId, result.Results[1].EventId);

        Assert.Equal(600d, await TestDatabase.ScalarAsync<double>("SELECT duration_seconds FROM application_sessions WHERE id = @i", ("i", appId)));
        Assert.Equal(1L, await Count("application_sessions", d));
    }

    [Fact]
    public async Task Backlog_of_old_events_does_not_overwrite_newer_live_state()
    {
        var (admin, d) = await NewDeviceAsync();
        var now = factory.Clock.GetUtcNow();
        var live = new LiveState(Presence.Active, "alice", 1, new ForegroundInfo(200, "chrome", null, "News"),
            [new OpenApplication(200, "chrome", null, "News", now)], 0, true, now, 10, 30, true);
        Assert.Equal(HttpStatusCode.OK, (await d.HeartbeatAsync(d.NewHeartbeat(now, live))).StatusCode);

        // Two days of backlog arrive after the heartbeat.
        var old = now.AddDays(-2);
        var result = await d.IngestAsync(
            d.Event(EventTypes.ApplicationForeground, old, P.Foreground(Guid.NewGuid(), "EXCEL.EXE", 100)),
            d.Event(EventTypes.SessionLock, old.AddMinutes(5), P.Session()),
            d.Event(EventTypes.SystemMetric, old.AddMinutes(6), P.Metric(cpu: 99)));
        Assert.Equal(3, result.Accepted);

        var card = (await admin.GetJsonAsync<List<LiveDeviceCard>>("/api/v1/live")).Single(c => c.DeviceId == d.DeviceId);
        Assert.Equal("chrome", card.CurrentApplication);
        Assert.Equal(Presence.Active, card.Presence);
        Assert.Equal(10, card.CpuPercent);
    }

    [Fact]
    public async Task Foreground_then_background_completes_one_application_session()
    {
        var (_, d) = await NewDeviceAsync();
        var appId = Guid.NewGuid();
        await d.IngestAsync(d.Event(EventTypes.ApplicationForeground, T0, P.Foreground(appId, "EXCEL.EXE", 100, "Q3.xlsx")));

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM application_sessions WHERE id = @i AND ended_at IS NULL", ("i", appId)));
        Assert.Equal("FOREGROUND", await TestDatabase.ScalarAsync<string>("SELECT session_type FROM application_sessions WHERE id = @i", ("i", appId)));

        var end = T0.AddMinutes(30);
        await d.IngestAsync(d.Event(EventTypes.ApplicationBackground, end, P.Background(appId, T0, end, interaction: 1500, idle: 120)));

        var row = await TestDatabase.ScalarAsync<string>(
            "SELECT concat(process_name, '|', duration_seconds, '|', interaction_seconds, '|', idle_seconds, '|', end_reason, '|', (ended_at IS NOT NULL)) FROM application_sessions WHERE id = @i", ("i", appId));
        Assert.Equal("EXCEL.EXE|1800|1500|120|SWITCHED|t", row);
        Assert.Equal(1L, await Count("application_sessions", d));
    }

    [Fact]
    public async Task Background_only_creates_a_complete_session()
    {
        var (_, d) = await NewDeviceAsync();
        var appId = Guid.NewGuid();
        var end = T0.AddMinutes(12);
        var result = await d.IngestAsync(d.Event(EventTypes.ApplicationBackground, end, P.Background(appId, T0, end, 300, process: "chrome.exe", pid: 7)));
        Assert.Equal(1, result.Accepted);
        Assert.Equal("chrome.exe|720|300", await TestDatabase.ScalarAsync<string>(
            "SELECT concat(process_name, '|', duration_seconds, '|', interaction_seconds) FROM application_sessions WHERE id = @i AND ended_at IS NOT NULL", ("i", appId)));
    }

    [Fact]
    public async Task Late_foreground_after_background_does_not_reopen_the_session()
    {
        var (_, d) = await NewDeviceAsync();
        var appId = Guid.NewGuid();
        var end = T0.AddMinutes(5);
        await d.IngestAsync(d.Event(EventTypes.ApplicationBackground, end, P.Background(appId, T0, end, 100), sequence: 50));
        await d.IngestAsync(d.Event(EventTypes.ApplicationForeground, T0, P.Foreground(appId), sequence: 49));

        Assert.Equal(1L, await Count("application_sessions", d));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM application_sessions WHERE id = @i AND ended_at IS NOT NULL AND duration_seconds = 300", ("i", appId)));
    }

    [Fact]
    public async Task Idle_and_lock_sessions_open_and_close_with_the_right_boundaries()
    {
        var (_, d) = await NewDeviceAsync();
        var idleSince = T0.AddMinutes(20);
        await d.IngestAsync(d.Event(EventTypes.UserLogin, T0, P.Session()),
            d.Event(EventTypes.IdleStarted, idleSince.AddMinutes(5), P.Idle(idleSince)));

        // The idle session starts at the last real input, not when the threshold elapsed, and is open.
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM idle_sessions i JOIN devices d ON d.id = i.device_id WHERE d.device_id = @d AND kind = 'IDLE' AND ended_at IS NULL AND started_at = @s", ("d", d.DeviceId), ("s", idleSince)));

        await d.IngestAsync(d.Event(EventTypes.IdleEnded, idleSince.AddMinutes(15), P.Idle(idleSince, 900)));
        Assert.Equal("900", await TestDatabase.ScalarAsync<string>(
            "SELECT duration_seconds::text FROM idle_sessions i JOIN devices d ON d.id = i.device_id WHERE d.device_id = @d AND kind = 'IDLE' AND ended_at IS NOT NULL", ("d", d.DeviceId)));

        var lockAt = T0.AddHours(1);
        await d.IngestAsync(d.Event(EventTypes.SessionLock, lockAt, P.Session()), d.Event(EventTypes.SessionUnlock, lockAt.AddMinutes(10), P.Session()));
        Assert.Equal(600d, await TestDatabase.ScalarAsync<double>(
            "SELECT duration_seconds FROM idle_sessions i JOIN devices d ON d.id = i.device_id WHERE d.device_id = @d AND kind = 'LOCKED'", ("d", d.DeviceId)));

        await d.IngestAsync(d.Event(EventTypes.SessionDisconnect, T0.AddHours(2), P.Session()), d.Event(EventTypes.SessionConnect, T0.AddHours(2).AddMinutes(3), P.Session()));
        Assert.Equal(180d, await TestDatabase.ScalarAsync<double>(
            "SELECT duration_seconds FROM idle_sessions i JOIN devices d ON d.id = i.device_id WHERE d.device_id = @d AND kind = 'DISCONNECTED'", ("d", d.DeviceId)));
    }

    [Fact]
    public async Task Idle_ended_without_started_and_repeated_started_do_not_duplicate_periods()
    {
        var (_, d) = await NewDeviceAsync();
        var since = T0.AddMinutes(10);
        // END first (its START was lost or arrives later).
        await d.IngestAsync(d.Event(EventTypes.IdleEnded, since.AddMinutes(8), P.Idle(since, 480)));
        await d.IngestAsync(d.Event(EventTypes.IdleStarted, since.AddMinutes(5), P.Idle(since)));

        Assert.Equal(1L, await Count("idle_sessions", d));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM idle_sessions i JOIN devices d ON d.id = i.device_id WHERE d.device_id = @d AND ended_at IS NOT NULL", ("d", d.DeviceId)));
    }

    [Fact]
    public async Task Input_activity_is_merged_per_bucket_and_never_stores_key_identities()
    {
        var (_, d) = await NewDeviceAsync();
        var bucket = T0.AddMinutes(3);
        await d.IngestAsync(d.Event(EventTypes.KeyboardActivity, bucket.AddSeconds(30), P.Keyboard(bucket, 40)));
        await d.IngestAsync(d.Event(EventTypes.MouseActivity, bucket.AddSeconds(30), P.Mouse(bucket, 120, 6, 3, 400, 300)));
        await d.IngestAsync(d.Event(EventTypes.KeyboardActivity, bucket.AddSeconds(31), P.Keyboard(bucket, 55)));   // cumulative counter grows
        await d.IngestAsync(d.Event(EventTypes.KeyboardActivity, bucket.AddSeconds(32), P.Keyboard(bucket, 10)));   // stale lower value must not shrink it
        await d.IngestAsync(d.Event(EventTypes.MouseActivity, bucket.AddSeconds(60), P.Mouse(bucket.AddSeconds(30), 5, 1)));   // next bucket

        Assert.Equal(2L, await Count("input_activity", d));
        var merged = await TestDatabase.ScalarAsync<string>(
            "SELECT concat(keyboard_activity, '|', mouse_activity, '|', click_count, '|', wheel_count, '|', optional_cursor_x, '|', optional_cursor_y) FROM input_activity a JOIN devices d ON d.id = a.device_id WHERE d.device_id = @d AND a.timestamp = @t",
            ("d", d.DeviceId), ("t", bucket));
        Assert.Equal("55|120|6|3|400|300", merged);

        // The schema simply has no column that could hold a typed character.
        var columns = await TestDatabase.ScalarAsync<string>("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_name = 'input_activity'");
        Assert.DoesNotContain("key_code", columns);
        Assert.DoesNotContain("text", columns);
    }

    [Fact]
    public async Task Logout_before_login_is_completed_by_the_late_login()
    {
        var (_, d) = await NewDeviceAsync();
        await d.IngestAsync(d.Event(EventTypes.UserLogout, T0.AddHours(2), P.Session()));
        await d.IngestAsync(d.Event(EventTypes.UserLogin, T0, P.Session()));

        Assert.Equal(1L, await Count("user_sessions", d));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM user_sessions s JOIN devices d ON d.id = s.device_id WHERE d.device_id = @d AND login_missing = false AND login_at = @l AND logout_at = @o",
            ("d", d.DeviceId), ("l", T0), ("o", T0.AddHours(2))));
    }

    [Fact]
    public async Task Process_lifetimes_and_raw_events_are_recorded_even_when_stop_arrives_first()
    {
        var (_, d) = await NewDeviceAsync();
        var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid();
        await d.IngestAsync(
            d.Event(EventTypes.ProcessStarted, T0, P.Process(p1, "notepad.exe", T0)),
            d.Event(EventTypes.ProcessStopped, T0.AddMinutes(4), P.Process(p1, "notepad.exe", T0, T0.AddMinutes(4))));
        await d.IngestAsync(
            d.Event(EventTypes.ProcessStopped, T0.AddMinutes(9), P.Process(p2, "calc.exe", T0.AddMinutes(5), T0.AddMinutes(9))),
            d.Event(EventTypes.ProcessStarted, T0.AddMinutes(5), P.Process(p2, "calc.exe", T0.AddMinutes(5)), sequence: 1));

        Assert.Equal(4L, await Count("process_events", d));
        Assert.Equal(2L, await Count("process_sessions", d));
        Assert.Equal(2L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM process_sessions s JOIN devices d ON d.id = s.device_id WHERE d.device_id = @d AND ended_at IS NOT NULL", ("d", d.DeviceId)));
    }

    [Fact]
    public async Task User_login_logout_usb_file_and_network_events_are_projected()
    {
        var (_, d) = await NewDeviceAsync();
        await d.IngestAsync(
            d.Event(EventTypes.UserLogin, T0, P.Session("carol")),
            d.Event(EventTypes.UsbConnected, T0.AddMinutes(1), P.Usb()),
            d.Event(EventTypes.UsbDisconnected, T0.AddMinutes(2), P.Usb()),
            d.Event(EventTypes.FileCreated, T0.AddMinutes(3), P.File()),
            d.Event(EventTypes.NetworkDisconnected, T0.AddMinutes(4), new NetworkPayload(false, [], null)),
            d.Event(EventTypes.UserLogout, T0.AddHours(1), P.Session("carol")));

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM user_sessions s JOIN devices d ON d.id = s.device_id WHERE d.device_id = @d AND logout_at IS NOT NULL AND login_missing = false", ("d", d.DeviceId)));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM device_users u JOIN devices d ON d.id = u.device_id WHERE d.device_id = @d AND windows_username = 'carol'", ("d", d.DeviceId)));
        Assert.Equal(2L, await Count("usb_events", d));
        Assert.Equal("CREATED", await TestDatabase.ScalarAsync<string>("SELECT action FROM file_events f JOIN devices d ON d.id = f.device_id WHERE d.device_id = @d", ("d", d.DeviceId)));
        Assert.Equal(6L, await Count("event_ingestion", d));
    }

    [Fact]
    public async Task Future_timestamps_use_receive_time_and_raise_CLOCK_SKEW()
    {
        var (admin, d) = await NewDeviceAsync();
        var now = factory.Clock.GetUtcNow();
        var future = now.AddHours(3);
        var nearFuture = now.AddMinutes(2);   // within tolerance: kept as sent
        var result = await d.IngestAsync(
            d.Event(EventTypes.UsbConnected, future, P.Usb()),
            d.Event(EventTypes.UsbDisconnected, nearFuture, P.Usb()));
        Assert.Equal(2, result.Accepted);

        var stored = await TestDatabase.ScalarAsync<string>(
            "SELECT string_agg(to_char(usb.timestamp AT TIME ZONE 'UTC', 'HH24:MI'), ',' ORDER BY usb.event_type) FROM usb_events usb JOIN devices d ON d.id = usb.device_id WHERE d.device_id = @d", ("d", d.DeviceId));
        Assert.Equal($"{now:HH:mm},{nearFuture:HH:mm}", stored);   // USB_CONNECTED -> receive time, USB_DISCONNECTED -> as sent
        var events = await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}&kind=CLOCK_SKEW");
        Assert.Equal(1, events.Total);
        Assert.Equal("WARNING", events.Items[0].Severity);
    }

    [Fact]
    public async Task Employee_is_resolved_from_device_then_code_hint_then_windows_user()
    {
        var admin = await factory.AdminAsync();
        // Device without employee: the envelope's employee code resolves it (employee must exist).
        await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("EMP-HINT", "Hint Person", "Ops", null, true));
        var d = await factory.EnrollDeviceAsync(admin, employeeCode: null);
        await d.IngestAsync(d.Event(EventTypes.UsbConnected, T0, P.Usb()) with { EmployeeId = "EMP-HINT" });
        await d.IngestAsync(d.Event(EventTypes.UsbConnected, T0.AddMinutes(1), P.Usb()));   // no hint, no mapping: unresolved

        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM usb_events u JOIN devices d ON d.id = u.device_id JOIN employees e ON e.id = u.employee_id WHERE d.device_id = @d AND e.employee_code = 'EMP-HINT'", ("d", d.DeviceId)));
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM usb_events u JOIN devices d ON d.id = u.device_id WHERE d.device_id = @d AND u.employee_id IS NULL", ("d", d.DeviceId)));
    }

    [Fact]
    public async Task Agent_lifecycle_events_update_status_and_raise_security_events()
    {
        var (admin, d) = await NewDeviceAsync();
        await d.IngestAsync(
            d.Event(EventTypes.AgentStarted, T0, P.Agent(unexpected: true)),
            d.Event(EventTypes.AgentError, T0.AddMinutes(1), P.Agent("USB watcher crashed", component: "UsbCollector")),
            d.Event(EventTypes.AgentVersionChanged, T0.AddMinutes(2), P.Agent(version: "1.4.2")));

        var events = await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}");
        Assert.Contains(events.Items, e => e.Kind == SecurityEventKinds.UnexpectedShutdown);
        Assert.Contains(events.Items, e => e.Kind == SecurityEventKinds.CollectorFailure && e.Message.Contains("USB watcher crashed"));
        var detail = await admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{d.DeviceId}");
        Assert.Equal("1.4.2", detail.Summary.AgentVersion);
    }
}
