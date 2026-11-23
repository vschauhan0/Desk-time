using System.Text.Json;
using System.Text.Json.Serialization;

namespace NISS.Monitor.Protocol;

/// <summary>Schema version constants.</summary>
public static class SchemaVersions
{
    public const int Current = 1;
}

/// <summary>
/// The common envelope every collector emits. The <see cref="Payload"/> shape depends on
/// <see cref="EventType"/> and is documented in docs/monitoring-events.md.
/// </summary>
/// <param name="EventId">Globally unique id. The server ingests idempotently on this value.</param>
/// <param name="SchemaVersion">Envelope+payload schema version.</param>
/// <param name="DeviceId">Server-assigned device id, e.g. "PC-024".</param>
/// <param name="EmployeeId">Employee code (e.g. "EMP1024") when known; the server also resolves it itself.</param>
/// <param name="SessionId">Agent-generated id of the interactive session context; null for service-level events.</param>
/// <param name="EventType">One of <see cref="EventTypes"/>.</param>
/// <param name="TimestampUtc">Event time, UTC, already corrected by the agent's measured clock offset.</param>
/// <param name="LocalTimestamp">Same instant with the device's local UTC offset (for presentation).</param>
/// <param name="Source">Producing component, see <see cref="EventSources"/>.</param>
/// <param name="Sequence">Monotonic per-device counter from the agent's local queue; defines upload ordering.</param>
/// <param name="ClockOffsetMs">Agent's estimate of (server clock - device clock) at creation time, in ms.</param>
/// <param name="Payload">Event-specific JSON object.</param>
public sealed record EventEnvelope(
    Guid EventId,
    int SchemaVersion,
    string DeviceId,
    string? EmployeeId,
    Guid? SessionId,
    string EventType,
    DateTimeOffset TimestampUtc,
    DateTimeOffset LocalTimestamp,
    string Source,
    long Sequence,
    long ClockOffsetMs,
    JsonElement Payload);

/// <summary>Shared System.Text.Json options used by every component so the wire format is identical.</summary>
public static class ProtocolJson
{
    /// <summary>camelCase, string enums, ignore nulls on write, case-insensitive on read.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Create a fresh copy of the options (e.g. for ASP.NET Core, which wants to own its instance).</summary>
    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>Serialize <paramref name="payload"/> into a detached <see cref="JsonElement"/>.</summary>
    public static JsonElement ToElement<T>(T payload) => JsonSerializer.SerializeToElement(payload, Options);

    /// <summary>Deserialize a payload element into <typeparamref name="T"/> (null when it does not match).</summary>
    public static T? FromElement<T>(JsonElement payload)
    {
        try { return payload.Deserialize<T>(Options); }
        catch (JsonException) { return default; }
    }
}
