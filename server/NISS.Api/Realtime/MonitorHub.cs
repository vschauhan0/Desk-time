using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NISS.Api.Security;
using NISS.Application.Abstractions;
using NISS.Application.Live;
using NISS.Monitor.Protocol;

namespace NISS.Api.Realtime;

/// <summary>Group names used by the hub.</summary>
public static class HubGroups
{
    public const string All = "all";
    public const string Feed = "feed";
    public static string Device(string deviceId) => $"device:{deviceId}";
    public static string Live(Guid liveSessionId) => $"live:{liveSessionId}";
}

/// <summary>
/// Dashboard real-time hub (<c>/hubs/monitor</c>). Every authenticated client joins group <c>all</c>; clients opt in to the
/// event feed (<c>feed</c>) or to one device's events (<c>device:{id}</c>).
/// </summary>
[Authorize(Policy = AuthDefaults.ViewerPolicy)]
public sealed partial class MonitorHub(ILiveViewService liveView, LiveSessionRegistry liveSessions) : Hub
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex DeviceIdPattern();

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.All);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A watcher that closed its tab / navigated away / crashed stops counting; end any session it was the last to watch.
        foreach (var sessionId in liveSessions.SessionsWatchedBy(Context.ConnectionId))
        {
            if (liveSessions.RemoveViewer(sessionId, Context.ConnectionId) == 0)
                await liveView.StopAsync(sessionId, CancellationToken.None);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public Task SubscribeDevice(string deviceId) => DeviceIdPattern().IsMatch(deviceId ?? "")
        ? Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Device(deviceId!))
        : throw new HubException("Invalid device id.");

    public Task UnsubscribeDevice(string deviceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Device(deviceId ?? ""));

    public Task SubscribeFeed() => Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Feed);

    public Task UnsubscribeFeed() => Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Feed);

    /// <summary>Join a live-view session's frame group. SUPERADMIN only; the id is an unguessable per-session guid.</summary>
    [Authorize(Policy = AuthDefaults.SuperAdminPolicy)]
    public async Task WatchLive(string liveSessionId)
    {
        if (!Guid.TryParse(liveSessionId, out var id)) throw new HubException("Invalid live session id.");
        liveSessions.AddViewer(id, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Live(id));
    }

    [Authorize(Policy = AuthDefaults.SuperAdminPolicy)]
    public async Task StopWatchLive(string liveSessionId)
    {
        if (!Guid.TryParse(liveSessionId, out var id)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Live(id));
        if (liveSessions.RemoveViewer(id, Context.ConnectionId) == 0)
            await liveView.StopAsync(id, CancellationToken.None);
    }
}

/// <summary>Publishes application notifications through SignalR.</summary>
public sealed class SignalRNotifier(IHubContext<MonitorHub> hub) : IRealtimeNotifier
{
    public Task DeviceStateChangedAsync(LiveDeviceCard card, CancellationToken ct = default) =>
        hub.Clients.Group(HubGroups.All).SendAsync(HubMethods.DeviceStateChanged, card, ct);

    public Task DeviceStatusChangedAsync(DeviceStatusChange change, CancellationToken ct = default) =>
        hub.Clients.Group(HubGroups.All).SendAsync(HubMethods.DeviceStatusChanged, change, ct);

    public Task SecurityEventRaisedAsync(SecurityEventDto dto, CancellationToken ct = default) =>
        hub.Clients.Group(HubGroups.All).SendAsync(HubMethods.SecurityEventRaised, dto, ct);

    public Task EventIngestedAsync(EventDto dto, CancellationToken ct = default) =>
        hub.Clients.Groups(HubGroups.Feed, HubGroups.Device(dto.DeviceId)).SendAsync(HubMethods.EventIngested, dto, ct);
}
