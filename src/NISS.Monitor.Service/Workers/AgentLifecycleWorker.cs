using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Infrastructure.Runtime;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Service.Workers;

/// <summary>
/// Agent lifecycle: initialises storage, optional unattended enrollment, and reports how the agent started and
/// stopped. If the previous run did not shut down cleanly (crash, power loss, killed) the next AGENT_STARTED says so,
/// which is how the server learns "the service failed and recovered". On graceful stop it queues AGENT_STOPPED and
/// makes a best-effort final upload so the dashboard shows SERVICE_STOPPED immediately.
/// </summary>
public sealed class AgentLifecycleWorker : BackgroundService
{
    private const string CleanKey = "clean_shutdown";
    private const string AliveKey = "last_alive_utc";
    private const string VersionKey = "last_version";
    private readonly SqliteDatabase _db;
    private readonly IAgentStateStore _state;
    private readonly DeviceIdentityStore _identity;
    private readonly IDeviceEnrollmentService _enrollment;
    private readonly IEventFactory _factory;
    private readonly IEventQueue _queue;
    private readonly IEventSink _sink;
    private readonly IUploader _uploader;
    private readonly ClockOffsetTracker _clock;
    private readonly AgentSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public AgentLifecycleWorker(SqliteDatabase db, IAgentStateStore state, DeviceIdentityStore identity, IDeviceEnrollmentService enrollment, IEventFactory factory,
        IEventQueue queue, IEventSink sink, IUploader uploader, ClockOffsetTracker clock, IOptions<AgentSettings> settings, TimeProvider time, ILoggerFactory logs)
    {
        _db = db; _state = state; _identity = identity; _enrollment = enrollment; _factory = factory; _queue = queue; _sink = sink;
        _uploader = uploader; _clock = clock; _settings = settings.Value; _time = time;
        _log = logs.CreateLogger("NISS.Lifecycle");
    }

    private bool _unexpectedPrevious;
    private DateTimeOffset? _lastAlive;
    private string? _previousVersion;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _db.EnsureInitialized();
        if (long.TryParse(await _state.GetAsync("clock_offset_ms", cancellationToken).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off))
            _clock.Restore(off);

        var clean = await _state.GetAsync(CleanKey, cancellationToken).ConfigureAwait(false);
        _unexpectedPrevious = clean == "0";
        if (DateTimeOffset.TryParse(await _state.GetAsync(AliveKey, cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var alive))
            _lastAlive = alive.ToUniversalTime();
        _previousVersion = await _state.GetAsync(VersionKey, cancellationToken).ConfigureAwait(false);
        await _state.SetAsync(CleanKey, "0", cancellationToken).ConfigureAwait(false);

        _log.LogWarning("NISS Monitor service {Version} starting (previous shutdown: {Previous})", AgentVersion.Current, _unexpectedPrevious ? "UNEXPECTED" : "clean");
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 1. make sure we are enrolled (unattended token, if configured)
            while (_identity.Reload() is null && !stoppingToken.IsCancellationRequested)
            {
                var token = _settings.Enrollment.Token;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        await _enrollment.EnrollAsync(new EnrollmentRequestOptions(_settings.Server.BaseUrl, token,
                            string.IsNullOrWhiteSpace(_settings.Enrollment.EmployeeId) ? null : _settings.Enrollment.EmployeeId,
                            _settings.Server.CertificateThumbprint), stoppingToken).ConfigureAwait(false);
                        break;
                    }
                    catch (InvalidOperationException ex) { _log.LogWarning("Automatic enrollment failed: {Message}", ex.Message); }
                }
                else _log.LogWarning("Device is not enrolled. Run: NISSAgent.exe --enroll --server <url> --token <one-time-token>");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }

            // 2. report start / version change / unexpected previous shutdown
            var now = _time.GetUtcNow();
            await _sink.EmitAsync(EventDraft.Create(EventTypes.AgentStarted, EventSources.Service, now,
                new AgentPayload(AgentVersion.Current, _previousVersion, "SERVICE_START", null, "Service", _unexpectedPrevious, _lastAlive)), stoppingToken).ConfigureAwait(false);
            if (_previousVersion is not null && _previousVersion != AgentVersion.Current)
                await _sink.EmitAsync(EventDraft.Create(EventTypes.AgentVersionChanged, EventSources.Service, now,
                    new AgentPayload(AgentVersion.Current, _previousVersion, "UPGRADE", null, "Service", null, null)), stoppingToken).ConfigureAwait(false);
            await _state.SetAsync(VersionKey, AgentVersion.Current, stoppingToken).ConfigureAwait(false);

            // 3. "last known alive" marker: lets the next start estimate when an unclean shutdown happened
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                await _state.SetAsync(AliveKey, _time.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture), stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identity.Current is not null)
            {
                // Tell a benign OS shutdown apart from a deliberate stop, and attribute the latter to whoever is
                // signed in - the server escalates only when InteractiveUser is set (see StopClassifier / the design doc).
                var report = StopClassifier.Classify(SystemStopInfo.IsShuttingDown(), SystemStopInfo.ConsoleUser());
                var message = report.InteractiveUser is null
                    ? "Graceful shutdown"
                    : $"Service stopped while {report.InteractiveUser} was signed in";
                var draft = EventDraft.Create(EventTypes.AgentStopped, EventSources.Service, _time.GetUtcNow(),
                    new AgentPayload(AgentVersion.Current, null, report.Reason, message, "Service", null, _time.GetUtcNow(), report.InteractiveUser));
                var env = await _factory.CreateAsync(draft, CancellationToken.None).ConfigureAwait(false);
                if (env is not null) await _queue.EnqueueBatchAsync([env], CancellationToken.None).ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _uploader.UploadPendingAsync(cts.Token).ConfigureAwait(false); // best effort: tell the server right now
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning("Final stop report could not be delivered: {Message}", ex.Message);
        }
        finally
        {
            await _state.SetAsync(CleanKey, "1", CancellationToken.None).ConfigureAwait(false);
            _log.LogWarning("NISS Monitor service stopped");
        }
    }
}
