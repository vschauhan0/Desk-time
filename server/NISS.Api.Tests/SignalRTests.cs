using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Health;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class SignalRTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A hub client on the in-memory test server that records every server-to-client message.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        public HubConnection Connection { get; }
        public Channel<LiveDeviceCard> States { get; } = Channel.CreateUnbounded<LiveDeviceCard>();
        public Channel<DeviceStatusChange> StatusChanges { get; } = Channel.CreateUnbounded<DeviceStatusChange>();
        public Channel<SecurityEventDto> Security { get; } = Channel.CreateUnbounded<SecurityEventDto>();
        public Channel<EventDto> Events { get; } = Channel.CreateUnbounded<EventDto>();

        public Listener(ApiFactory factory, string? token)
        {
            Connection = new HubConnectionBuilder()
                .WithUrl(new Uri(factory.Server.BaseAddress, HubMethods.Route), o =>
                {
                    o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .AddJsonProtocol(o => o.PayloadSerializerOptions = ProtocolJson.Create())
                .Build();
            Connection.On<LiveDeviceCard>(HubMethods.DeviceStateChanged, c => States.Writer.TryWrite(c));
            Connection.On<DeviceStatusChange>(HubMethods.DeviceStatusChanged, c => StatusChanges.Writer.TryWrite(c));
            Connection.On<SecurityEventDto>(HubMethods.SecurityEventRaised, e => Security.Writer.TryWrite(e));
            Connection.On<EventDto>(HubMethods.EventIngested, e => Events.Writer.TryWrite(e));
        }

        public static async Task<T> NextAsync<T>(Channel<T> channel, Func<T, bool>? match = null)
        {
            using var cts = new CancellationTokenSource(Wait);
            await foreach (var item in channel.Reader.ReadAllAsync(cts.Token))
                if (match is null || match(item)) return item;
            throw new TimeoutException("No matching message arrived.");
        }

        public static async Task<bool> NothingAsync<T>(Channel<T> channel, Func<T, bool> match, TimeSpan window)
        {
            using var cts = new CancellationTokenSource(window);
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cts.Token))
                    if (match(item)) return false;
            }
            catch (OperationCanceledException) { }
            return true;
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private async Task<Listener> ConnectAsync(HttpClient? admin = null)
    {
        var (_, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        var listener = new Listener(factory, auth.AccessToken);
        await listener.Connection.StartAsync();
        return listener;
    }

    [Fact]
    public async Task Hub_rejects_anonymous_connections()
    {
        await using var anonymous = new Listener(factory, null);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.Connection.StartAsync());
    }

    [Fact]
    public async Task Ingested_events_are_pushed_to_the_feed_and_the_device_card_is_refreshed()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-SIG");
        await using var client = await ConnectAsync();
        await client.Connection.InvokeAsync("SubscribeFeed");

        var now = factory.Clock.GetUtcNow();
        var appId = Guid.NewGuid();
        var batch = await d.IngestAsync(
            d.Event(EventTypes.SystemMetric, now.AddSeconds(-2), P.Metric()),
            d.Event(EventTypes.ApplicationForeground, now.AddSeconds(-1), P.Foreground(appId, "EXCEL.EXE", 100, "Budget.xlsx")));
        Assert.Equal(2, batch.Accepted);

        var evt = await Listener.NextAsync(client.Events, e => e.DeviceId == d.DeviceId);
        Assert.Equal(EventTypes.ApplicationForeground, evt.EventType);
        Assert.Equal("EMP-SIG", evt.EmployeeCode);
        Assert.Equal("EXCEL.EXE", evt.Payload.GetProperty("processName").GetString());

        var card = await Listener.NextAsync(client.States, c => c.DeviceId == d.DeviceId && c.CurrentApplication == "EXCEL.EXE");
        Assert.Equal("Budget.xlsx", card.CurrentWindow);
        Assert.Equal("EMP-SIG", card.EmployeeCode);

        // Metric events never go to the event feed.
        Assert.True(await Listener.NothingAsync(client.Events, e => e.EventType == EventTypes.SystemMetric, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Device_subscription_receives_only_that_devices_events()
    {
        var admin = await factory.AdminAsync();
        var a = await factory.EnrollDeviceAsync(admin);
        var b = await factory.EnrollDeviceAsync(admin);
        await using var client = await ConnectAsync();
        await client.Connection.InvokeAsync("SubscribeDevice", a.DeviceId);

        var now = factory.Clock.GetUtcNow();
        await b.IngestAsync(b.Event(EventTypes.UsbConnected, now, P.Usb("other device")));
        await a.IngestAsync(a.Event(EventTypes.UsbConnected, now, P.Usb("mine")));

        var evt = await Listener.NextAsync(client.Events);
        Assert.Equal(a.DeviceId, evt.DeviceId);
        Assert.True(await Listener.NothingAsync(client.Events, e => e.DeviceId == b.DeviceId, TimeSpan.FromMilliseconds(500)));

        await client.Connection.InvokeAsync("UnsubscribeDevice", a.DeviceId);
        await a.IngestAsync(a.Event(EventTypes.UsbDisconnected, now.AddSeconds(1), P.Usb("mine")));
        Assert.True(await Listener.NothingAsync(client.Events, e => e.DeviceId == a.DeviceId, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Heartbeats_and_admin_actions_push_state_and_status_changes()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-SIG2");
        await using var client = await ConnectAsync();

        var now = factory.Clock.GetUtcNow();
        var live = new LiveState(Presence.Active, "alice", 1, new ForegroundInfo(5, "code.exe", null, "Program.cs"), [new OpenApplication(5, "code.exe", null, "Program.cs", now)], 0, true, now, 5, 40, true);
        await d.HeartbeatAsync(d.NewHeartbeat(now, live));

        var state = await Listener.NextAsync(client.States, c => c.DeviceId == d.DeviceId && c.Status == DeviceHealth.Online);
        Assert.Equal("code.exe", state.CurrentApplication);
        var change = await Listener.NextAsync(client.StatusChanges, c => c.DeviceId == d.DeviceId);
        Assert.Equal((DeviceHealth.Online, DeviceHealth.Pending), (change.Status, change.PreviousStatus));

        await admin.PostAsync($"/api/v1/devices/{d.DeviceId}/disable", null);
        var disabled = await Listener.NextAsync(client.StatusChanges, c => c.DeviceId == d.DeviceId && c.Status == DeviceHealth.Disabled);
        Assert.Equal(DeviceHealth.Online, disabled.PreviousStatus);
        await Listener.NextAsync(client.States, c => c.DeviceId == d.DeviceId && c.Status == DeviceHealth.Disabled);
    }

    [Fact]
    public async Task Health_worker_transitions_publish_status_changes_and_security_events()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin);
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        await using var client = await ConnectAsync();

        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        using (var scope = factory.Scope())
            await scope.ServiceProvider.GetRequiredService<IDeviceHealthService>().RunOnceAsync(CancellationToken.None);

        var change = await Listener.NextAsync(client.StatusChanges, c => c.DeviceId == d.DeviceId);
        Assert.Equal(DeviceHealth.Offline, change.Status);
        var alert = await Listener.NextAsync(client.Security, e => e.DeviceId == d.DeviceId);
        Assert.Equal(SecurityEventKinds.DeviceOffline, alert.Kind);
        Assert.Equal("CRITICAL", alert.Severity);
    }
}
