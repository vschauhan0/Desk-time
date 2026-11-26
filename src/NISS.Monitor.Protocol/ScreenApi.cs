namespace NISS.Monitor.Protocol;

/// <summary>Which capture a screen frame belongs to. Periodic frames are stored; live frames are relayed only.</summary>
public enum ScreenPipeKind
{
    Periodic,
    Live,
}

/// <summary>
/// Metadata that precedes the JPEG bytes of one captured screen frame as it travels
/// session helper -&gt; service (binary pipe) and, for periodic frames, service -&gt; server.
/// </summary>
public sealed record ScreenFrameHeader(
    ScreenPipeKind Kind,
    int Monitor,
    int Width,
    int Height,
    long CapturedAtUtcTicks,
    int ByteLength);

/// <summary>Actions the server sends down the live socket to drive a live view session.</summary>
public static class LiveControlAction
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Switch = "switch";
}

/// <summary>
/// Control message the server pushes to the agent to start/stop live capture or switch the
/// watched monitor. <see cref="Action"/> is one of <see cref="LiveControlAction"/>.
/// </summary>
public sealed record LiveControlMessage(
    string Action,
    Guid LiveSessionId,
    int Monitor,
    int Fps,
    int Quality);
