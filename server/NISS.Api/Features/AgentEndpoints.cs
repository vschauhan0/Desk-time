using System.Security.Claims;
using NISS.Api.Http;
using NISS.Api.Security;
using NISS.Application.Devices;
using NISS.Application.Ingestion;
using NISS.Monitor.Protocol;

namespace NISS.Api.Features;

/// <summary>Endpoints called by the endpoint agent: enroll, credentials, heartbeat, event ingestion, config, status.</summary>
public static class AgentEndpoints
{
    public static void MapAgent(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/enroll", async (EnrollRequest request, IEnrollmentService enrollment, CancellationToken ct) =>
                Results.Ok(await enrollment.EnrollAsync(request, ct)))
            .AllowAnonymous().RequireRateLimiting(RateLimiting.Enroll).WithValidation<EnrollRequest>();

        api.MapPost("/devices/credentials/rotate", async (ClaimsPrincipal user, IDeviceCommandService commands, CancellationToken ct) =>
                Results.Ok(await commands.RotateSecretAsync(user.GetDeviceId(), ct)))
            .RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest);

        Delegate heartbeat = async (HeartbeatRequest request, ClaimsPrincipal user, IAgentService agent, CancellationToken ct) =>
            Results.Ok(await agent.HeartbeatAsync(user.GetDeviceId(), request, ct));
        foreach (var route in new[] { "/devices/heartbeat", "/heartbeat" })
            api.MapPost(route, heartbeat).RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest).WithValidation<HeartbeatRequest>();

        api.MapPost("/events/batch", async (EventBatchRequest request, ClaimsPrincipal user, IEventIngestionService ingestion, CancellationToken ct) =>
                Results.Ok(await ingestion.IngestAsync(user.GetDeviceId(), request, metricsOnly: false, ct)))
            .RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest).WithValidation<EventBatchRequest>();

        Delegate metrics = async (EventBatchRequest request, ClaimsPrincipal user, IEventIngestionService ingestion, CancellationToken ct) =>
            Results.Ok(await ingestion.IngestAsync(user.GetDeviceId(), request, metricsOnly: true, ct));
        foreach (var route in new[] { "/metrics/batch", "/metrics" })
            api.MapPost(route, metrics).RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest).WithValidation<EventBatchRequest>();

        api.MapGet("/agent/config", async (ClaimsPrincipal user, IAgentService agent, CancellationToken ct) =>
                Results.Ok(await agent.GetConfigAsync(user.GetDeviceId(), ct)))
            .RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest);

        api.MapPost("/agent/status", async (AgentStatusRequest request, ClaimsPrincipal user, IAgentService agent, CancellationToken ct) =>
            {
                await agent.UpdateStatusAsync(user.GetDeviceId(), request, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest).WithValidation<AgentStatusRequest>();
    }
}

/// <summary>/enrollment-tokens (ADMIN+).</summary>
public static class EnrollmentTokenEndpoints
{
    public static void MapEnrollmentTokens(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/enrollment-tokens").RequireAuthorization(AuthDefaults.AdminPolicy);

        g.MapGet("", async (IEnrollmentService enrollment, CancellationToken ct) => Results.Ok(await enrollment.ListTokensAsync(ct)));

        g.MapPost("", async (EnrollmentTokenRequest body, IEnrollmentService enrollment, CancellationToken ct) =>
        {
            var token = await enrollment.CreateTokenAsync(body, ct);
            return Results.Created($"/api/v1/enrollment-tokens/{token.Id}", token);
        }).WithValidation<EnrollmentTokenRequest>().RequirePageEdit("settings.tokens");

        g.MapDelete("/{id:guid}", async (Guid id, IEnrollmentService enrollment, CancellationToken ct) =>
        {
            await enrollment.DeleteTokenAsync(id, ct);
            return Results.NoContent();
        }).RequirePageEdit("settings.tokens");
    }
}
