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
}
