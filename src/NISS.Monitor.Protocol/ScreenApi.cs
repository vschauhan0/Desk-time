namespace NISS.Monitor.Protocol;

/// <summary>Which capture a screen frame belongs to. Periodic frames are stored; live frames are relayed only.</summary>
public enum ScreenPipeKind
{
    Periodic,
    Live,
}

/// <summary>What caused a stored screenshot to be taken.</summary>
public static class ScreenTriggers
{
    /// <summary>The ordinary timed interval.</summary>
    public const string Periodic = "PERIODIC";

    /// <summary>A flagged term matched the foreground window.</summary>
    public const string Flagged = "FLAGGED";

    /// <summary>True when <paramref name="trigger"/> is a value this build understands.</summary>
    public static bool IsKnown(string? trigger) => trigger is Periodic or Flagged;
}

/// <summary>Where a flagged term was recognised.</summary>
public static class FlagMatchKinds
{
    /// <summary>Matched the foreground window title (how a website is recognised).</summary>
    public const string Title = "TITLE";

    /// <summary>Matched the foreground process name.</summary>
    public const string Process = "PROCESS";
}

/// <summary>
/// Metadata that precedes the JPEG bytes of one captured screen frame as it travels
/// session helper -&gt; service (binary pipe) and, for periodic frames, service -&gt; server.
/// </summary>
/// <param name="Trigger">One of <see cref="ScreenTriggers"/>. Defaults to periodic so older senders stay valid.</param>
/// <param name="FlagTerm">For a flagged capture, the configured term that matched.</param>
/// <param name="MatchedOn">For a flagged capture, one of <see cref="FlagMatchKinds"/>.</param>
/// <param name="ClipId">Set on every frame of one clip; groups the burst together.</param>
/// <param name="ClipSeq">0-based index of this frame within its clip.</param>
/// <param name="ClipTotal">How many frames the clip is expected to contain.</param>
/// <param name="SessionUser">
/// Windows username of the captured session. It travels on the frame because only the session helper knows it: the
/// service runs as LocalSystem and would otherwise attribute every shared-PC capture to SYSTEM.
/// </param>
public sealed record ScreenFrameHeader(
    ScreenPipeKind Kind,
    int Monitor,
    int Width,
    int Height,
    long CapturedAtUtcTicks,
    int ByteLength,
    string Trigger = ScreenTriggers.Periodic,
    string? FlagTerm = null,
    string? MatchedOn = null,
    Guid? ClipId = null,
    int ClipSeq = 0,
    int ClipTotal = 0,
    string? SessionUser = null);

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

/// <summary>Kinds of <see cref="ScreenCommand"/> the service sends down to the session helper.</summary>
public static class ScreenCommandKind
{
    public const string SetPolicy = "set-policy";
    public const string LiveStart = "live-start";
    public const string LiveStop = "live-stop";
    public const string LiveSwitch = "live-switch";
}

/// <summary>Service -&gt; session control over the local screen-control pipe: either an updated policy or a live command.</summary>
public sealed record ScreenCommand(string Kind, ScreenCapturePolicy? Policy, LiveControlMessage? Live);
