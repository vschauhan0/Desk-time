using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Live;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Ingestion;

/// <summary>Idempotent, ordered, transactional event ingestion.</summary>
public interface IEventIngestionService
{
    /// <summary>
    /// Ingests one batch for the authenticated device. Events are processed in ascending <c>sequence</c> inside one
    /// transaction; a malformed event is REJECTED individually and never fails the batch.
    /// </summary>
    Task<EventBatchResponse> IngestAsync(Guid deviceId, EventBatchRequest request, bool metricsOnly, CancellationToken ct);
}

internal sealed class EventIngestionService(
    IAppDbContext db,
    IIngestionStore store,
    IEnumerable<IEventProjector> projectors,
    IEmployeeResolver employees,
    ISecurityEventService security,
    INotificationBuffer buffer,
    ILiveStateService live,
    TimeProvider clock,
    ILogger<EventIngestionService> log) : IEventIngestionService
{
    public const int MaxBatchSize = 500;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset EarliestPlausible = new(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, IEventProjector> _projectors = projectors
        .SelectMany(p => p.EventTypes.Select(t => (Type: t, Projector: p)))
        .ToDictionary(x => x.Type, x => x.Projector, StringComparer.Ordinal);

    private sealed record WorkItem(int Index, EventEnvelope Envelope, PayloadInfo Info, DateTimeOffset Timestamp, bool Skewed);

    public async Task<EventBatchResponse> IngestAsync(Guid deviceId, EventBatchRequest request, bool metricsOnly, CancellationToken ct)
    {
        var receivedAt = clock.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (device.Status is DeviceHealth.Disabled or DeviceHealth.Revoked)
            throw new ForbiddenException($"Device is {device.Status}.");
        if (!string.Equals(request.DeviceId, device.DeviceCode, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("deviceId does not match the authenticated device.", nameof(request.DeviceId));
        if (request.Events.Count > MaxBatchSize)
            throw new BadRequestException($"A batch may contain at most {MaxBatchSize} events.", nameof(request.Events));

        var results = new EventResult[request.Events.Count];
        var work = new List<WorkItem>(request.Events.Count);
        for (var i = 0; i < request.Events.Count; i++)
        {
            var env = request.Events[i];
            var error = ValidateEnvelope(env, device, metricsOnly, receivedAt, out var info, out var ts, out var skewed);
            if (error is not null) results[i] = new EventResult(env?.EventId ?? Guid.Empty, EventResultStatus.Rejected, error);
            else work.Add(new WorkItem(i, env!, info, ts, skewed));
        }

        var ordered = work.OrderBy(w => w.Envelope.Sequence).ThenBy(w => w.Index).ToList();
        var accepted = new List<EventIngestion>();
        var skewCount = 0;
        var deviceEntityId = device.Id;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await store.LockDeviceAsync(device.DeviceCode, ct);
            var n = 0;
            foreach (var item in ordered)
            {
                var savepoint = "ev_" + n++;
                await tx.CreateSavepointAsync(savepoint, ct);
                try
                {
                    var env = item.Envelope;
                    var employeeId = await employees.ResolveAsync(device, env.EmployeeId, item.Info.UserName, ct);
                    var row = new EventIngestion
                    {
                        EventId = env.EventId, DeviceId = device.Id, EmployeeId = employeeId, SessionId = env.SessionId, EventType = env.EventType,
                        Timestamp = item.Timestamp, ReceivedAt = receivedAt, Sequence = env.Sequence, Source = Text.Trunc(env.Source, 100) ?? "",
                        SchemaVersion = env.SchemaVersion, ClockOffsetMs = env.ClockOffsetMs,
                        LocalOffsetMinutes = (int)env.LocalTimestamp.Offset.TotalMinutes, Subject = Text.Trunc(item.Info.Subject, 260),
                        Payload = env.Payload.GetRawText(),
                    };

                    if (!await store.TryInsertAsync(row, ct))
                    {
                        results[item.Index] = new EventResult(env.EventId, EventResultStatus.Duplicate, null);
                        await tx.ReleaseSavepointAsync(savepoint, ct);
                        continue;
                    }

                    if (_projectors.TryGetValue(env.EventType, out var projector))
                        await projector.ProjectAsync(new ProjectionContext(device, employeeId, env, item.Timestamp, receivedAt), ct);

                    if (device.LastEventAt is null || item.Timestamp > device.LastEventAt) device.LastEventAt = item.Timestamp;
                    if (env.LocalTimestamp != default) device.LocalOffsetMinutes = row.LocalOffsetMinutes;
                    await db.SaveChangesAsync(ct);
                    await tx.ReleaseSavepointAsync(savepoint, ct);

                    results[item.Index] = new EventResult(env.EventId, EventResultStatus.Accepted, null);
                    accepted.Add(row);
                    if (item.Skewed) skewCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not DbException and not DbUpdateException)
                {
                    // A logic failure for this one event: undo its partial writes and reject only it.
                    log.LogWarning(ex, "Event {EventId} ({EventType}) of device {DeviceCode} could not be projected", item.Envelope.EventId, item.Envelope.EventType, device.DeviceCode);
                    await tx.RollbackToSavepointAsync(savepoint, ct);
                    db.ChangeTracker.Clear();
                    buffer.Clear();
                    device = await db.Devices.FirstAsync(d => d.Id == deviceEntityId, ct);
                    results[item.Index] = new EventResult(item.Envelope.EventId, EventResultStatus.Rejected, "event could not be processed");
                }
            }

            if (skewCount > 0)
            {
                await security.RaiseAsync(new DeviceRef(device.Id, device.DeviceCode), SecurityEventKinds.ClockSkew, SecuritySeverity.Warning,
                    $"{device.DeviceCode}: {skewCount} event(s) carried a timestamp more than 5 minutes in the future; receive time was used.",
                    onlyIfNoneOpen: false, throttle: TimeSpan.FromHours(1), ct: ct);
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        // After commit: push to dashboards.
        foreach (var row in accepted.Where(r => r.EventType != EventTypes.SystemMetric))
            buffer.Add(EventMapper.ToDto(row, device.DeviceCode, await employees.CodeAsync(row.EmployeeId, ct)));
        await buffer.FlushAsync(ct);
        if (accepted.Count > 0) await live.PublishAsync(device.Id, ct);

        var resultList = results.Select(r => r ?? new EventResult(Guid.Empty, EventResultStatus.Rejected, "not processed")).ToList();
        return new EventBatchResponse(
            resultList.Count(r => r.Status == EventResultStatus.Accepted),
            resultList.Count(r => r.Status == EventResultStatus.Duplicate),
            resultList.Count(r => r.Status == EventResultStatus.Rejected),
            resultList, clock.GetUtcNow());
    }

    private static string? ValidateEnvelope(EventEnvelope? env, Device device, bool metricsOnly, DateTimeOffset now,
        out PayloadInfo info, out DateTimeOffset timestamp, out bool skewed)
    {
        info = default; timestamp = default; skewed = false;
        if (env is null) return "event is null";
        if (env.EventId == Guid.Empty) return "eventId is required";
        if (env.SchemaVersion != SchemaVersions.Current) return $"unsupported schemaVersion {env.SchemaVersion}";
        if (!EventTypes.IsKnown(env.EventType)) return "unknown eventType";
        if (metricsOnly && env.EventType != EventTypes.SystemMetric) return "only SYSTEM_METRIC events are accepted on this endpoint";
        if (!string.Equals(env.DeviceId, device.DeviceCode, StringComparison.OrdinalIgnoreCase)) return "deviceId does not match the authenticated device";
        if (env.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined) return "payload is required";
        if (env.TimestampUtc < EarliestPlausible) return "timestampUtc is missing or implausible";

        info = PayloadCatalog.Inspect(env.EventType, env.Payload);
        if (info.Error is not null) return info.Error;

        timestamp = env.TimestampUtc.ToUniversalTime();
        if (timestamp > now + MaxFutureSkew) { timestamp = now; skewed = true; }
        return null;
    }
}

/// <summary>Maps stored events to the dashboard DTO.</summary>
public static class EventMapper
{
    public static EventDto ToDto(EventIngestion row, string deviceCode, string? employeeCode) =>
        new(row.EventId, row.EventType, row.Timestamp, row.LocalOffsetMinutes, deviceCode, employeeCode, row.Source, JsonColumn.ToElement(row.Payload));
}
