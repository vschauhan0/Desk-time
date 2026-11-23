using System.Text.Json;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Abstractions;

/// <summary>A long-running data collector. A failing collector must never take the agent down.</summary>
public interface ICollector
{
    /// <summary>Stable name used in health reporting and logs.</summary>
    string Name { get; }

    /// <summary>Run until <paramref name="cancellationToken"/> is cancelled. Throwing means "restart me with backoff".</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Watches process start/stop (service side). Emits PROCESS_STARTED / PROCESS_STOPPED.</summary>
public interface IProcessMonitor : ICollector;

/// <summary>Watches device arrival/removal. Emits USB_CONNECTED / USB_DISCONNECTED.</summary>
public interface IUsbMonitor : ICollector;

/// <summary>Optional, administrator-configured file metadata watcher. Off by default.</summary>
public interface IFileActivityMonitor : ICollector;

/// <summary>Point-in-time machine health sampling.</summary>
public interface ISystemMetricsCollector
{
    /// <summary>Sample CPU, memory, disks, network, battery and displays.</summary>
    SystemMetricPayload Collect();
}

/// <summary>Reads the foreground window of the interactive desktop (session process side).</summary>
public interface IForegroundMonitor
{
    /// <summary>The application that currently owns the foreground window, or null (locked, secure desktop, none).</summary>
    ForegroundSample? Sample(bool captureTitle);

    /// <summary>Applications (processes with a visible top-level window) currently open in this session.</summary>
    IReadOnlyList<OpenWindowApp> EnumerateOpenApplications(bool captureTitle);
}

/// <summary>Measures how long the user has produced no input (all devices, system-wide).</summary>
public interface IIdleMonitor
{
    /// <summary>Time since the last keyboard/mouse/touch input.</summary>
    TimeSpan GetIdleTime();
}

/// <summary>Counts keyboard/mouse activity. Never observes which keys were pressed.</summary>
public interface IInputActivityMonitor
{
    /// <summary>Feeds counters into the aggregator until cancelled (runs the OS message pump on its own thread).</summary>
    Task RunAsync(InputBucketAggregator aggregator, CancellationToken cancellationToken);
}

/// <summary>Detects interactive session lock/unlock (session process side).</summary>
public interface ISessionLockMonitor
{
    /// <summary>Raised on the pump thread with true = locked, false = unlocked.</summary>
    event Action<bool>? LockStateChanged;

    /// <summary>True when the session is currently locked.</summary>
    bool IsLocked { get; }
}

/// <summary>Periodic heartbeat to the server (also measures clock offset and picks up policy changes).</summary>
public interface IHeartbeatService
{
    /// <summary>Send one heartbeat. Returns false when the server could not be reached or rejected it.</summary>
    Task<bool> SendAsync(CancellationToken cancellationToken);
}

/// <summary>Uploads queued events in order with retry/backoff.</summary>
public interface IUploader
{
    /// <summary>Upload as much of the pending queue as possible. Never throws for network problems.</summary>
    Task<UploadResult> UploadPendingAsync(CancellationToken cancellationToken);
}

/// <summary>Outcome of one upload pass.</summary>
public sealed record UploadResult(int Uploaded, int Duplicates, int DeadLettered, bool ServerReachable, string? Error, bool AuthFailure);

/// <summary>Enrolls this device with the central server and stores the per-device credential.</summary>
public interface IDeviceEnrollmentService
{
    /// <summary>Current identity, or null when the device is not enrolled.</summary>
    Task<DeviceIdentity?> GetIdentityAsync(CancellationToken cancellationToken);

    /// <summary>Enroll using a one-time token. The secret is protected with machine-scope DPAPI before it is stored.</summary>
    Task<DeviceIdentity> EnrollAsync(EnrollmentRequestOptions options, CancellationToken cancellationToken);

    /// <summary>Rotate the per-device credential.</summary>
    Task RotateCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>Inputs for enrollment.</summary>
public sealed record EnrollmentRequestOptions(string ServerBaseUrl, string EnrollmentToken, string? EmployeeId, string? CertificateThumbprint);

/// <summary>Where the collector output goes. In the service it is the queue; in the session process it is the pipe to the service.</summary>
public interface IEventSink
{
    /// <summary>Submit an event draft. The service turns drafts into fully-formed envelopes.</summary>
    ValueTask EmitAsync(EventDraft draft, CancellationToken cancellationToken = default);
}

/// <summary>Screenshots are NOT collected in V1. The interface exists so a policy-driven module can be added later.</summary>
public interface IScreenCaptureService
{
    /// <summary>False in V1 (disabled by default; only explicit future policy may enable it).</summary>
    bool IsEnabled { get; }

    /// <summary>Capture a screenshot for an explicit, policy-approved reason. V1 always returns <see cref="ScreenCaptureResult.Disabled"/>.</summary>
    Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureMode mode, string reason, CancellationToken cancellationToken);
}

/// <summary>Future screenshot policy modes.</summary>
public enum ScreenCaptureMode { Disabled, Manual, IncidentBased, Scheduled }

/// <summary>Result of a capture attempt.</summary>
public sealed record ScreenCaptureResult(bool Captured, string Message)
{
    public static ScreenCaptureResult Disabled { get; } = new(false, "Screen capture is not enabled.");
}

/// <summary>Browser monitoring is out of scope for V1 and reports "Not Enabled".</summary>
public interface IBrowserMonitoringProvider
{
    /// <summary>Always false in V1.</summary>
    bool IsEnabled { get; }

    /// <summary>Human-readable status ("Not Enabled").</summary>
    string Status { get; }
}

/// <summary>An event before the service applies identity, sequence, and clock correction.</summary>
/// <param name="TimestampUtc">Raw device clock time of the occurrence.</param>
/// <param name="EventId">Optional deterministic id (used to make replays idempotent).</param>
public sealed record EventDraft(
    string EventType,
    string Source,
    DateTimeOffset TimestampUtc,
    JsonElement Payload,
    Guid? SessionId = null,
    Guid? EventId = null)
{
    /// <summary>Create a draft from a strongly typed payload.</summary>
    public static EventDraft Create<T>(string eventType, string source, DateTimeOffset timestampUtc, T payload, Guid? sessionId = null, Guid? eventId = null)
        => new(eventType, source, timestampUtc, ProtocolJson.ToElement(payload), sessionId, eventId);
}
