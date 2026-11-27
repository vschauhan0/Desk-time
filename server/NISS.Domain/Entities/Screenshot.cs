namespace NISS.Domain.Entities;

/// <summary>
/// One stored periodic screenshot. The JPEG bytes live on disk at <see cref="RelPath"/> (relative to the
/// configured screenshot root); this row is the metadata index. Live-view frames are never stored here.
/// </summary>
public sealed class Screenshot
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? EmployeeId { get; set; }
    /// <summary>Windows username of the captured session (for shared-PC attribution); may be empty.</summary>
    public string SessionUser { get; set; } = "";
    public DateTimeOffset CapturedAtUtc { get; set; }
    public int MonitorIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ByteSize { get; set; }
    /// <summary>Path of the JPEG file relative to the screenshot storage root.</summary>
    public string RelPath { get; set; } = "";
    /// <summary>Agent-supplied id used to make ingestion idempotent (unique per device).</summary>
    public string FrameId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Why this frame was captured: PERIODIC (the timed interval) or FLAGGED (a flagged term matched).</summary>
    public string Trigger { get; set; } = "PERIODIC";

    /// <summary>For a flagged capture, the configured term that matched; null otherwise.</summary>
    public string? FlagTerm { get; set; }

    /// <summary>For a flagged capture, where the term matched: TITLE or PROCESS.</summary>
    public string? MatchedOn { get; set; }

    /// <summary>
    /// Set on every frame of one clip and null on single stills. A clip is a burst of JPEGs the dashboard replays;
    /// there is no encoded video file.
    /// </summary>
    public Guid? ClipId { get; set; }

    /// <summary>0-based position of this frame within its clip.</summary>
    public int ClipSeq { get; set; }

    /// <summary>Frames the clip was expected to contain (the agent may emit fewer if the session locked mid-clip).</summary>
    public int ClipTotal { get; set; }
}
