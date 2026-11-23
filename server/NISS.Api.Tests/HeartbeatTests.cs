using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class HeartbeatTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Admin, TestDevice Device)> NewDeviceAsync(string? employee = "EMP-HB")
    {
        var admin = await factory.AdminAsync();
        return (admin, await factory.EnrollDeviceAsync(admin, employee));
    }

    private static async Task<Paged<SecurityEventDto>> Issues(HttpClient admin, TestDevice d, string kind, bool? resolved = null) =>
        await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}&kind={kind}" + (resolved is null ? "" : $"&resolved={resolved.ToString()!.ToLowerInvariant()}"));

    [Fact]
    public async Task First_heartbeat_makes_the_device_online_and_stores_a_heartbeat_row()
    {
        var (admin, d) = await NewDeviceAsync();
        Assert.Equal(DeviceHealth.Pending, (await admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{d.DeviceId}")).Summary.Status);

        var now = factory.Clock.GetUtcNow();
        var request = d.NewHeartbeat(now, version: "1.2.3") with { ClockOffsetMs = -180, ConfigVersion = 1 };
        var response = await (await d.HeartbeatAsync(request)).ReadAsync<HeartbeatResponse>();

        Assert.Equal("ACTIVE", response.DeviceStatus);
        Assert.Equal(now, response.ServerTimeUtc);
        Assert.True(response.ConfigVersion >= 1);

        var detail = await admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{d.DeviceId}");
        Assert.Equal(DeviceHealth.Online, detail.Summary.Status);
        Assert.Equal("1.2.3", detail.Summary.AgentVersion);
        Assert.InRange(Math.Abs((detail.Summary.LastSeenUtc!.Value - now).TotalMilliseconds), 0, 1);   // PostgreSQL keeps microseconds
        Assert.Equal(1L, await TestDatabase.ScalarAsync<long>("SELECT count(*) FROM heartbeats h JOIN devices d ON d.id = h.device_id WHERE d.device_id = @d", ("d", d.DeviceId)));

        var diagnostics = await admin.GetJsonAsync<DeviceDiagnostics>($"/api/v1/devices/{d.DeviceId}/diagnostics");
        Assert.Equal(-180, diagnostics.ClockOffsetMs);
        Assert.Equal("CONNECTED", diagnostics.SessionAgentState);
        Assert.True(diagnostics.ServerConnection);
    }

    [Fact]
    public async Task Live_state_keeps_running_foreground_and_interaction_separate()
    {
        var (admin, d) = await NewDeviceAsync();
        var now = factory.Clock.GetUtcNow();
        var apps = new[]
        {
            new OpenApplication(100, "EXCEL.EXE", null, "Q3.xlsx", now.AddHours(-2)),
            new OpenApplication(200, "chrome.exe", null, "News", now.AddHours(-3)),
            new OpenApplication(300, "notepad.exe", null, "notes.txt", now.AddMinutes(-5)),
        };
        var live = new LiveState(Presence.Active, "alice", 1, new ForegroundInfo(100, "EXCEL.EXE", null, "Q3.xlsx"), apps, 3, Interacting: true, now, 12.5, 61.2, true);
        await d.HeartbeatAsync(d.NewHeartbeat(now, live));

        var card = (await admin.GetJsonAsync<List<LiveDeviceCard>>("/api/v1/live")).Single(c => c.DeviceId == d.DeviceId);
        Assert.Equal("EXCEL.EXE", card.CurrentApplication);
        Assert.Equal("Q3.xlsx", card.CurrentWindow);
        Assert.Equal(3, card.Applications.Count);
        Assert.All(card.Applications, a => Assert.True(a.Running));                                             // running = every open app
        Assert.Equal(["EXCEL.EXE"], card.Applications.Where(a => a.Foreground).Select(a => a.ProcessName));    // foreground = only Excel
        Assert.Equal(["EXCEL.EXE"], card.Applications.Where(a => a.Interaction).Select(a => a.ProcessName));   // interaction = foreground AND interacting
        Assert.True(card.IsInteracting);
        Assert.False(card.IsIdle);
        Assert.Equal(12.5, card.CpuPercent);
        Assert.Equal("alice", card.UserName);
        Assert.Equal("EMP-HB", card.EmployeeCode);

        // Foreground app but the user stopped interacting: still foreground, no interaction.
        await d.HeartbeatAsync(d.NewHeartbeat(now.AddSeconds(30), live with { Interacting = false, IdleSeconds = 60 }));
        card = (await admin.GetJsonAsync<List<LiveDeviceCard>>("/api/v1/live")).Single(c => c.DeviceId == d.DeviceId);
        Assert.Single(card.Applications, a => a.Foreground);
        Assert.DoesNotContain(card.Applications, a => a.Interaction);
        Assert.False(card.IsInteracting);

        // Idle presence.
        await d.HeartbeatAsync(d.NewHeartbeat(now.AddSeconds(60), live with { Presence = Presence.Idle, Interacting = false, IdleSeconds = 400 }));
        card = (await admin.GetJsonAsync<List<LiveDeviceCard>>("/api/v1/live")).Single(c => c.DeviceId == d.DeviceId);
        Assert.True(card.IsIdle);
        Assert.Equal(Presence.Idle, card.Presence);
    }

    [Fact]
    public async Task Queue_backlog_raises_and_then_resolves_AGENT_BACKLOG_WARNING()
    {
        var (admin, d) = await NewDeviceAsync();
        var now = factory.Clock.GetUtcNow();
        await d.HeartbeatAsync(d.NewHeartbeat(now, queueDepth: 6000));
        await d.HeartbeatAsync(d.NewHeartbeat(now.AddSeconds(30), queueDepth: 6500));   // still open: no second event

        var open = await Issues(admin, d, "AGENT_BACKLOG_WARNING", resolved: false);
        Assert.Equal(1, open.Total);

        await d.HeartbeatAsync(d.NewHeartbeat(now.AddSeconds(60), queueDepth: 12));
        Assert.Equal(0, (await Issues(admin, d, "AGENT_BACKLOG_WARNING", resolved: false)).Total);
        Assert.Equal(1, (await Issues(admin, d, "AGENT_BACKLOG_WARNING", resolved: true)).Total);
    }

    [Fact]
    public async Task Missing_session_agent_while_a_user_is_logged_in_raises_SESSION_AGENT_MISSING()
    {
        var (admin, d) = await NewDeviceAsync();
        var now = factory.Clock.GetUtcNow();
        var live = new LiveState(Presence.Active, "alice", 1, null, [], 0, false, null, null, null, true);

        await d.HeartbeatAsync(d.NewHeartbeat(now, live, agents: []));
        Assert.Equal(1, (await Issues(admin, d, "SESSION_AGENT_MISSING", resolved: false)).Total);

        // Nobody logged in: not an issue. The event resolves once the agent is back.
        await d.HeartbeatAsync(d.NewHeartbeat(now.AddSeconds(30), live, agents: [new SessionAgentInfo(1, "alice", true, now)]));
        Assert.Equal(0, (await Issues(admin, d, "SESSION_AGENT_MISSING", resolved: false)).Total);
    }

    [Fact]
    public async Task Outdated_agent_versions_are_flagged_against_the_configured_minimum()
    {
        var (admin, d) = await NewDeviceAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/v1/settings/health-thresholds", new HealthThresholds(60, 300, 5000, "2.0.0"))).StatusCode);
        var now = factory.Clock.GetUtcNow();

        var response = await (await d.HeartbeatAsync(d.NewHeartbeat(now, version: "1.9.9"))).ReadAsync<HeartbeatResponse>();
        Assert.Equal("2.0.0", response.MinAgentVersion);
        Assert.Equal(1, (await Issues(admin, d, "AGENT_OUTDATED", resolved: false)).Total);

        await d.HeartbeatAsync(d.NewHeartbeat(now.AddSeconds(30), version: "2.1.0"));
        Assert.Equal(0, (await Issues(admin, d, "AGENT_OUTDATED", resolved: false)).Total);
        await admin.PutJsonAsync("/api/v1/settings/health-thresholds", new HealthThresholds(60, 300, 5000, null));
    }

    [Fact]
    public async Task Agent_status_updates_diagnostics_and_upload_failures_raise_events()
    {
        var (admin, d) = await NewDeviceAsync();
        var now = factory.Clock.GetUtcNow();
        var status = new AgentStatusRequest(d.DeviceId, "1.0.0",
            [new CollectorStatus("Process", "Running", null, now), new CollectorStatus("Usb", "Failed", "access denied", now)],
            42, 3, now.AddMinutes(-5), now.AddMinutes(-1), "connection refused", null, false, [new SessionAgentInfo(1, "alice", true, now)],
            "Windows 11", "DESK-HB", "ACME", "M1", "SER123", 20, 55, 71);
        Assert.Equal(HttpStatusCode.NoContent, (await d.Client.PostJsonAsync("/api/v1/agent/status", status)).StatusCode);

        var diagnostics = await admin.GetJsonAsync<DeviceDiagnostics>($"/api/v1/devices/{d.DeviceId}/diagnostics");
        Assert.Equal(42, diagnostics.QueueCount);
        Assert.Equal(3, diagnostics.DeadLetterCount);
        Assert.Equal(71, diagnostics.DiskUsedPercent);
        Assert.Equal(2, diagnostics.Collectors.Count);
        Assert.Equal("access denied", diagnostics.Collectors.Single(c => c.Name == "Usb").LastError);
        Assert.Equal(1, (await Issues(admin, d, "UPLOAD_FAILURE", resolved: false)).Total);
        Assert.Equal(1, (await Issues(admin, d, "COLLECTOR_FAILURE")).Total);
    }

    [Fact]
    public async Task Agent_config_returns_the_effective_policy()
    {
        var (_, d) = await NewDeviceAsync();
        var config = await (await d.Client.GetAsync("/api/v1/agent/config")).ReadAsync<AgentConfigResponse>();
        Assert.Equal(d.DeviceId, config.DeviceId);
        Assert.Equal("EMP-HB", config.EmployeeId);
        Assert.Equal(30, config.Policy.HeartbeatSeconds);
        Assert.Equal(5, config.Policy.IdleThresholdMinutes);
        Assert.False(config.Policy.CaptureCommandLines);
    }

    [Fact]
    public async Task Heartbeat_for_another_device_is_refused()
    {
        var (_, d) = await NewDeviceAsync();
        var response = await d.Client.PostJsonAsync("/api/v1/devices/heartbeat", d.NewHeartbeat(factory.Clock.GetUtcNow()) with { DeviceId = "PC-999" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await d.Client.PostJsonAsync("/api/v1/heartbeat", d.NewHeartbeat(factory.Clock.GetUtcNow()))).StatusCode);   // alias route
    }
}
