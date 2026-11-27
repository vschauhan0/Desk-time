using System.Text.Json;
using NISS.Monitor.Protocol;

namespace NISS.Application.Ingestion;

/// <summary>Outcome of inspecting an event payload: an error (the event is rejected) or a denormalised subject.</summary>
public readonly record struct PayloadInfo(string? Error, string? Subject, string? UserName)
{
    public static PayloadInfo Ok(string? subject = null, string? userName = null) => new(null, subject, userName);
    public static PayloadInfo Fail(string error) => new(error, null, null);
}

/// <summary>Maps each event type to its payload record and the minimum a payload must contain to be projected.</summary>
public static class PayloadCatalog
{
    private static readonly Dictionary<string, Func<JsonElement, PayloadInfo>> Inspectors = Build();

    /// <summary>Validates a payload for an event type. Unknown types are reported as errors.</summary>
    public static PayloadInfo Inspect(string eventType, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return PayloadInfo.Fail("payload must be a JSON object");
        return Inspectors.TryGetValue(eventType, out var inspect) ? inspect(payload) : PayloadInfo.Fail("unknown eventType");
    }

    private static Func<JsonElement, PayloadInfo> Check<T>(Func<T, PayloadInfo> extra) where T : class =>
        el =>
        {
            var value = ProtocolJson.FromElement<T>(el);
            return value is null ? PayloadInfo.Fail("payload does not match the schema of this eventType") : extra(value);
        };

    private static PayloadInfo Need(bool ok, string message, string? subject = null, string? user = null) =>
        ok ? PayloadInfo.Ok(subject, user) : PayloadInfo.Fail(message);

    private static Dictionary<string, Func<JsonElement, PayloadInfo>> Build()
    {
        var d = new Dictionary<string, Func<JsonElement, PayloadInfo>>(StringComparer.Ordinal);

        Func<JsonElement, PayloadInfo> process = Check<ProcessPayload>(p =>
            Need(p.ProcessSessionId != Guid.Empty && !string.IsNullOrWhiteSpace(p.ProcessName), "processSessionId and processName are required", p.ProcessName, p.UserName));
        d[EventTypes.ProcessStarted] = process;
        d[EventTypes.ProcessStopped] = process;

        Func<JsonElement, PayloadInfo> lifecycle = Check<ApplicationLifecyclePayload>(p =>
            Need(!string.IsNullOrWhiteSpace(p.ProcessName), "processName is required", p.ProcessName));
        d[EventTypes.ApplicationStarted] = lifecycle;
        d[EventTypes.ApplicationStopped] = lifecycle;

        d[EventTypes.ApplicationForeground] = Check<ApplicationForegroundPayload>(p =>
            Need(p.AppSessionId != Guid.Empty && !string.IsNullOrWhiteSpace(p.ProcessName), "appSessionId and processName are required", p.ProcessName));
        d[EventTypes.ApplicationBackground] = Check<ApplicationBackgroundPayload>(p =>
            Need(p.AppSessionId != Guid.Empty && !string.IsNullOrWhiteSpace(p.ProcessName) && p.StartedUtc != default && p.EndedUtc != default,
                "appSessionId, processName, startedUtc and endedUtc are required", p.ProcessName));

        Func<JsonElement, PayloadInfo> session = Check<SessionStatePayload>(p => PayloadInfo.Ok(null, p.UserName));
        foreach (var t in new[] { EventTypes.UserLogin, EventTypes.UserLogout, EventTypes.SessionLock, EventTypes.SessionUnlock, EventTypes.SessionConnect, EventTypes.SessionDisconnect })
            d[t] = session;

        Func<JsonElement, PayloadInfo> idle = Check<IdlePayload>(p => PayloadInfo.Ok(null, p.UserName));
        foreach (var t in new[] { EventTypes.ActiveStarted, EventTypes.IdleStarted, EventTypes.IdleEnded }) d[t] = idle;

        Func<JsonElement, PayloadInfo> input = Check<InputActivityPayload>(p =>
            Need(p.BucketStartUtc != default, "bucketStartUtc is required"));
        d[EventTypes.MouseActivity] = input;
        d[EventTypes.KeyboardActivity] = input;

        Func<JsonElement, PayloadInfo> usb = Check<UsbPayload>(p => Need(!string.IsNullOrWhiteSpace(p.DeviceType), "deviceType is required", p.Product ?? p.Vendor));
        d[EventTypes.UsbConnected] = usb;
        d[EventTypes.UsbDisconnected] = usb;

        Func<JsonElement, PayloadInfo> file = Check<FilePayload>(p => Need(!string.IsNullOrWhiteSpace(p.Path), "path is required", null, p.UserName));
        foreach (var t in new[] { EventTypes.FileCreated, EventTypes.FileModified, EventTypes.FileRenamed, EventTypes.FileDeleted }) d[t] = file;

        d[EventTypes.SystemMetric] = Check<SystemMetricPayload>(_ => PayloadInfo.Ok());

        // Periodic hardware + installed-software snapshot. All fields optional, so any well-formed payload is accepted.
        d[EventTypes.DeviceInventory] = Check<InventoryPayload>(_ => PayloadInfo.Ok());

        Func<JsonElement, PayloadInfo> network = Check<NetworkPayload>(_ => PayloadInfo.Ok());
        d[EventTypes.NetworkConnected] = network;
        d[EventTypes.NetworkDisconnected] = network;

        Func<JsonElement, PayloadInfo> agent = Check<AgentPayload>(p => PayloadInfo.Ok(p.Component));
        foreach (var t in new[] { EventTypes.AgentStarted, EventTypes.AgentStopped, EventTypes.AgentHeartbeat, EventTypes.AgentError, EventTypes.AgentVersionChanged })
            d[t] = agent;

        return d;
    }
}
