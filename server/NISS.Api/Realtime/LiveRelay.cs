using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using NISS.Application.Live;
using NISS.Monitor.Protocol;

namespace NISS.Api.Realtime;

internal sealed class LiveRelay(IHubContext<MonitorHub> hub) : ILiveRelay
{
    private readonly ConcurrentDictionary<Guid, ILiveAgentChannel> _agents = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sendGate = new();

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

    public async Task PushFrameAsync(Guid liveSessionId, int monitor, byte[] jpeg, CancellationToken ct)
    {
        // Latest-frame-wins: if a send to this session's group is still in flight, drop this frame rather than queue it.
        var gate = _sendGate.GetOrAdd(liveSessionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            await hub.Clients.Group(HubGroups.Live(liveSessionId))
                .SendAsync(HubMethods.ScreenFrame, new ScreenFrameMessage(liveSessionId, monitor, Convert.ToBase64String(jpeg)), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
