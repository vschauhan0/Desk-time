using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NISS.Api.Security;
using NISS.Application.Abstractions;
using NISS.Monitor.Protocol;

namespace NISS.Api.Realtime;

/// <summary>Group names used by the hub.</summary>
public static class HubGroups
{
    public const string All = "all";
    public const string Feed = "feed";
    public static string Device(string deviceId) => $"device:{deviceId}";
}

/// <summary>
/// Dashboard real-time hub (<c>/hubs/monitor</c>). Every authenticated client joins group <c>all</c>; clients opt in to the
/// event feed (<c>feed</c>) or to one device's events (<c>device:{id}</c>).
/// </summary>
[Authorize(Policy = AuthDefaults.ViewerPolicy)]
public sealed partial class MonitorHub : Hub
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex DeviceIdPattern();

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.All);
        await base.OnConnectedAsync();
    }

    public Task SubscribeDevice(string deviceId) => DeviceIdPattern().IsMatch(deviceId ?? "")
        ? Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Device(deviceId!))
        : throw new HubException("Invalid device id.");

    public Task UnsubscribeDevice(string deviceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Device(deviceId ?? ""));

    public Task SubscribeFeed() => Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Feed);

    public Task UnsubscribeFeed() => Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.Feed);
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
