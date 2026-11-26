using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using NISS.Monitor.Protocol;

namespace NISS.Api.Realtime;

/// <summary>A live agent connection (the service's websocket) that control messages can be sent down.</summary>
public interface ILiveAgentChannel
{
    Guid DeviceId { get; }
    Task SendControlAsync(LiveControlMessage message, CancellationToken ct);
}

/// <summary>
/// Relays live screen frames from agent websockets to the watching dashboards, and control messages the other way.
/// Frames for a session go only to the SignalR group <c>live:{liveSessionId}</c> that the authorized SUPERADMIN joined,
/// so one device's frames never reach a watcher of another.
/// </summary>
public interface ILiveRelay
{
    void RegisterAgent(ILiveAgentChannel channel);
    void UnregisterAgent(ILiveAgentChannel channel);
    bool IsDeviceConnected(Guid deviceId);
    Task<bool> SendControlAsync(Guid deviceId, LiveControlMessage message, CancellationToken ct);
    Task PushFrameAsync(Guid liveSessionId, int monitor, byte[] jpeg, CancellationToken ct);
}

internal sealed class LiveRelay(IHubContext<MonitorHub> hub) : ILiveRelay
{
    private readonly ConcurrentDictionary<Guid, ILiveAgentChannel> _agents = new();

    public void RegisterAgent(ILiveAgentChannel channel) => _agents[channel.DeviceId] = channel;

    public void UnregisterAgent(ILiveAgentChannel channel) =>
        _agents.TryRemove(new KeyValuePair<Guid, ILiveAgentChannel>(channel.DeviceId, channel));

    public bool IsDeviceConnected(Guid deviceId) => _agents.ContainsKey(deviceId);

    public async Task<bool> SendControlAsync(Guid deviceId, LiveControlMessage message, CancellationToken ct)
    {
        if (!_agents.TryGetValue(deviceId, out var channel)) return false;
        await channel.SendControlAsync(message, ct);
        return true;
    }

    public Task PushFrameAsync(Guid liveSessionId, int monitor, byte[] jpeg, CancellationToken ct) =>
        hub.Clients.Group(HubGroups.Live(liveSessionId))
            .SendAsync(HubMethods.ScreenFrame, new ScreenFrameMessage(liveSessionId, monitor, Convert.ToBase64String(jpeg)), ct);
}
