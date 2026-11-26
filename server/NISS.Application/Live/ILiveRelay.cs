using NISS.Monitor.Protocol;

namespace NISS.Application.Live;

/// <summary>A live agent connection (the service's websocket) that control messages can be sent down.</summary>
public interface ILiveAgentChannel
{
    Guid DeviceId { get; }
    Task SendControlAsync(LiveControlMessage message, CancellationToken ct);
}

/// <summary>
/// Relays live screen frames from agent websockets to the watching dashboards, and control messages the other way.
/// Frames for a session go only to the watchers that joined that session's group, so one device's frames never reach
/// a watcher of another. Implemented in the API layer over SignalR.
/// </summary>
public interface ILiveRelay
{
    void RegisterAgent(ILiveAgentChannel channel);
    void UnregisterAgent(ILiveAgentChannel channel);
    bool IsDeviceConnected(Guid deviceId);
    Task<bool> SendControlAsync(Guid deviceId, LiveControlMessage message, CancellationToken ct);
    Task PushFrameAsync(Guid liveSessionId, int monitor, byte[] jpeg, CancellationToken ct);
}
