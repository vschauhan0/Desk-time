using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using NISS.Application.Live;
using NISS.Monitor.Protocol;

namespace NISS.Api.Realtime;

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
