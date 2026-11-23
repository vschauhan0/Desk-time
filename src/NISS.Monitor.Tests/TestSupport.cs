using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

/// <summary>An in-memory stand-in for the NISS API used by the agent tests. Can be taken offline.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    private readonly object _gate = new();
    public bool Online { get; set; } = true;
    public HttpStatusCode? ForceStatus { get; set; }
    public string DeviceId { get; set; } = "PC-024";
    public string DeviceStatus { get; set; } = "ACTIVE";
    public int ConfigVersion { get; set; } = 1;
    public TimeSpan ServerClockAhead { get; set; } = TimeSpan.Zero;
    public HashSet<Guid> Stored { get; } = [];
    public List<Guid> ReceivedOrder { get; } = [];
    public List<int> BatchSizes { get; } = [];
    public int EnrollCalls { get; private set; }
    public int HeartbeatCalls { get; private set; }
    public string? LastAuthorization { get; private set; }
    public HashSet<string> RejectTypes { get; } = [];
    public HashSet<Guid> PoisonIds { get; } = [];
    public HeartbeatRequest? LastHeartbeat { get; private set; }
    public FakeTimeProvider Time { get; set; } = new(DateTimeOffset.UtcNow);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Online) throw new HttpRequestException("No route to host (simulated outage)");
        LastAuthorization = request.Headers.Authorization?.ToString();
        var path = request.RequestUri!.AbsolutePath;
        if (ForceStatus is { } forced) return new HttpResponseMessage(forced) { Content = new StringContent("forced") };

        switch (path)
        {
            case "/api/v1/health":
                return Json(new HealthResponse("Healthy", "1.0.0", Now(), true));
            case "/api/v1/devices/enroll":
            {
                EnrollCalls++;
                var req = await request.Content!.ReadFromJsonAsync<EnrollRequest>(ProtocolJson.Options, cancellationToken);
                if (req!.EnrollmentToken != "GOOD-TOKEN") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return Json(new EnrollResponse(DeviceId, "s3cret-value", req.EmployeeId ?? "EMP1024", Now(), new AgentPolicy { Version = 1 }));
            }
            case "/api/v1/devices/heartbeat":
            {
                HeartbeatCalls++;
                LastHeartbeat = await request.Content!.ReadFromJsonAsync<HeartbeatRequest>(ProtocolJson.Options, cancellationToken);
                return Json(new HeartbeatResponse(Now(), DeviceStatus, ConfigVersion, null, null));
            }
            case "/api/v1/agent/config":
                return Json(new AgentConfigResponse(DeviceId, "EMP1024", Now(), new AgentPolicy { Version = ConfigVersion, IdleThresholdMinutes = 7 }));
            case "/api/v1/events/batch":
            {
                var batch = await request.Content!.ReadFromJsonAsync<EventBatchRequest>(ProtocolJson.Options, cancellationToken);
                lock (_gate)
                {
                    BatchSizes.Add(batch!.Events.Count);
                    if (batch.Events.Any(e => PoisonIds.Contains(e.EventId)) && batch.Events.Count > 1)
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad batch") };

                    var results = new List<EventResult>();
                    foreach (var e in batch.Events.OrderBy(e => e.Sequence))
                    {
                        if (PoisonIds.Contains(e.EventId) || RejectTypes.Contains(e.EventType))
                        {
                            if (batch.Events.Count == 1 && PoisonIds.Contains(e.EventId)) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad event") };
                            results.Add(new EventResult(e.EventId, EventResultStatus.Rejected, "invalid"));
                        }
                        else if (Stored.Add(e.EventId)) { ReceivedOrder.Add(e.EventId); results.Add(new EventResult(e.EventId, EventResultStatus.Accepted, null)); }
                        else results.Add(new EventResult(e.EventId, EventResultStatus.Duplicate, null));
                    }
                    return Json(new EventBatchResponse(results.Count(r => r.Status == "ACCEPTED"), results.Count(r => r.Status == "DUPLICATE"),
                        results.Count(r => r.Status == "REJECTED"), results, Now()));
                }
            }
            case "/api/v1/agent/status":
                return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private DateTimeOffset Now() => Time.GetUtcNow() + ServerClockAhead;

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ProtocolJson.Options) };
}

/// <summary>A fully wired agent (real SQLite + DPAPI + HTTP client) pointed at a <see cref="FakeServer"/> and a temp data folder.</summary>
public sealed class AgentTestHost : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "niss-agent-tests", Guid.NewGuid().ToString("N"));
    public FakeServer Server { get; }
    public FakeTimeProvider Time { get; }
    public ServiceProvider Services { get; }

    public AgentTestHost(FakeServer? server = null, Action<IServiceCollection>? extra = null, Dictionary<string, string?>? config = null)
    {
        Directory.CreateDirectory(Dir);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
        Server = server ?? new FakeServer();
        Server.Time = Time;

        var settings = new Dictionary<string, string?>
        {
            ["Agent:Server:BaseUrl"] = "https://niss.test:8443",
            ["Agent:Storage:DataDirectory"] = Dir,
            ["Agent:Session:PipeName"] = "NISSTest." + Guid.NewGuid().ToString("N"),
            ["Agent:Session:RequireTrustedClientPath"] = "false",
        };
        if (config is not null) foreach (var (k, v) in config) settings[k] = v;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddNissAgentInfrastructure(cfg);
        sc.AddSingleton<TimeProvider>(Time); // replaces TimeProvider.System
        sc.AddHttpClient<ServerApiClient>().ConfigurePrimaryHttpMessageHandler(() => Server);
        extra?.Invoke(sc);
        Services = sc.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Enroll against the fake server using the good token.</summary>
    public async Task<NISS.Monitor.Core.DeviceIdentity> EnrollAsync(string? employee = null)
        => await Get<IDeviceEnrollmentService>().EnrollAsync(new EnrollmentRequestOptions("https://niss.test:8443", "GOOD-TOKEN", employee, null), default);

    /// <summary>Create an envelope through the real factory and persist it in the local queue.</summary>
    public async Task<EventEnvelope> QueueAsync(string type, object? payload = null, DateTimeOffset? at = null)
    {
        var draft = EventDraft.Create(type, EventSources.Service, at ?? Time.GetUtcNow(), payload ?? new { });
        var env = await Get<IEventFactory>().CreateAsync(draft, default) ?? throw new InvalidOperationException("not enrolled");
        await Get<IEventQueue>().EnqueueAsync(env, default);
        return env;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
