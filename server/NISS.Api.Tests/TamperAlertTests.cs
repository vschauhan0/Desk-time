using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>
/// Layer-3 tamper alerting: a deliberate stop while a user is signed in becomes a Critical, attributed alert; a
/// benign shutdown stays the quiet ServiceStopped. See docs/superpowers/specs/2026-11-26-tamper-resistance-design.md.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TamperAlertTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<List<SecurityEventDto>> EventsAsync(HttpClient admin, TestDevice d) =>
        (await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}&pageSize=100")).Items.ToList();

    [Fact]
    public async Task Deliberate_stop_while_a_user_is_signed_in_raises_a_critical_attributed_alert()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP1");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        factory.Clock.Advance(TimeSpan.FromSeconds(20));

        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(),
            P.Agent(message: "stopped", interactiveUser: "CONTOSO\\jdoe")));

        var events = await EventsAsync(admin, d);
        var tamper = Assert.Single(events, e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Equal("CRITICAL", tamper.Severity);
        Assert.Null(tamper.ResolvedAtUtc);
        Assert.Contains("jdoe", tamper.Message);
        // The device is still shown as service-stopped (the health state is orthogonal to the tamper alert).
        Assert.Equal(DeviceHealth.ServiceStopped, (await admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{d.DeviceId}")).Summary.Status);
    }

    [Fact]
    public async Task Benign_shutdown_does_not_raise_a_tamper_alert()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP2");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        factory.Clock.Advance(TimeSpan.FromSeconds(20));

        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(), P.Agent())); // no interactive user => benign

        var events = await EventsAsync(admin, d);
        Assert.DoesNotContain(events, e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.ServiceStopped);
    }

    private async Task AssignUpdateAsync(TestDevice d)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var id = await db.Devices.Where(x => x.DeviceCode == d.DeviceId).Select(x => x.Id).SingleAsync();
        db.DeviceUpdateStates.Add(new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = id, AssignedVersion = "9.9.9.0", Phase = UpdatePhase.Applying, UpdatedAt = factory.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Stop_during_a_server_assigned_update_is_not_tamper()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP3");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        await AssignUpdateAsync(d);
        factory.Clock.Advance(TimeSpan.FromSeconds(20));

        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(),
            P.Agent(message: StopReasons.UpdateRestart, interactiveUser: "CONTOSO\\jdoe")));

        var events = await EventsAsync(admin, d);
        Assert.DoesNotContain(events, e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.ServiceStopped);
    }

    [Fact]
    public async Task Forged_update_hint_without_a_server_assigned_update_still_raises_tamper()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP4");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        factory.Clock.Advance(TimeSpan.FromSeconds(20));

        // The agent claims an update restart, but the server assigned nothing: the hint must not suppress the alert.
        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(),
            P.Agent(message: StopReasons.UpdateRestart, interactiveUser: "CONTOSO\\jdoe")));

        var tamper = Assert.Single(await EventsAsync(admin, d), e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Equal("CRITICAL", tamper.Severity);
    }
}
