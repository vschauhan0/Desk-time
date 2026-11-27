using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NISS.Api.Http;
using NISS.Api.Security;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Options;
using NISS.Application.Updates;
using NISS.Monitor.Protocol;

namespace NISS.Api.Features;

/// <summary>Agent update packages: signed-package upload (SUPERADMIN).</summary>
public static class AgentUpdateEndpoints
{
    /// <summary>Max accepted package size (bytes).</summary>
    private const long MaxPackageBytes = 200L * 1024 * 1024;

    public static void MapAgentUpdates(this RouteGroupBuilder api)
    {
        // Device-authed: a device may fetch only the version currently assigned to it.
        api.MapGet("/agent/update/{version}", async (string version, HttpContext http, ClaimsPrincipal user, IAgentUpdateService updates,
                IPackageStore store, PackageDownloadGate gate, IOptions<AgentUpdateOptions> options, CancellationToken ct) =>
            {
                var package = await updates.GetAssignedPackageAsync(user.GetDeviceId(), version, ct);   // 403 unless it is this device's target
                if (!gate.TryEnter())
                {
                    http.Response.Headers.RetryAfter = Math.Max(options.Value.DownloadRetryAfterSeconds, 1).ToString();
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                try
                {
                    await using var stream = store.OpenRead(package.RelPath);
                    http.Response.ContentType = "application/zip";
                    http.Response.ContentLength = stream.Length;
                    await stream.CopyToAsync(http.Response.Body, ct);
                    return Results.Empty;
                }
                catch (FileNotFoundException) when (!http.Response.HasStarted) { return Results.NotFound(); }
                catch (DirectoryNotFoundException) when (!http.Response.HasStarted) { return Results.NotFound(); }
                finally { gate.Exit(); }
            })
            .RequireAuthorization(AuthDefaults.DevicePolicy);

        // Read-only recovery probe for the watchdog: no heartbeat, no status/Online side effects. (Literal segment beats {version}.)
        api.MapGet("/agent/update/target", async (ClaimsPrincipal user, IAgentUpdateService updates, CancellationToken ct) =>
                Results.Ok(await updates.GetTargetAsync(user.GetDeviceId(), ct)))
            .RequireAuthorization(AuthDefaults.DevicePolicy);

        api.MapPost("/agent/update/status", async (UpdateStatusReport report, ClaimsPrincipal user, IAgentUpdateService updates, CancellationToken ct) =>
            {
                await updates.ReportStatusAsync(user.GetDeviceId(), report, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthDefaults.DevicePolicy).RequireRateLimiting(RateLimiting.Ingest);

        var rollouts = api.MapGroup("/agent-rollouts").RequireAuthorization(AuthDefaults.SuperAdminPolicy);
        rollouts.MapPost("", async (CreateRolloutRequest req, ICurrentUser user, IAgentUpdateService updates, CancellationToken ct) =>
        {
            var rollout = await updates.CreateRolloutAsync(req.Version, req.Notes, user.Username ?? "unknown", ct);
            return Results.Created($"/api/v1/agent-rollouts/{rollout.Id}", await updates.GetRolloutAsync(rollout.Id, ct));
        });
        rollouts.MapGet("", async (IAgentUpdateService updates, CancellationToken ct) => Results.Ok(await updates.ListRolloutsAsync(ct)));
        rollouts.MapGet("{id:guid}/devices", async (Guid id, IAgentUpdateService updates, CancellationToken ct) => Results.Ok(await updates.GetRolloutDevicesAsync(id, ct)));
        rollouts.MapGet("{id:guid}", async (Guid id, IAgentUpdateService updates, CancellationToken ct) => Results.Ok(await updates.GetRolloutAsync(id, ct)));
        rollouts.MapPost("{id:guid}/canary", async (Guid id, CanaryRequest req, ICurrentUser user, IAgentUpdateService updates, CancellationToken ct) =>
        {
            var ids = new List<Guid>();
            foreach (var raw in req.DeviceIds ?? [])
                ids.Add(Guid.TryParse(raw, out var g) ? g : throw new BadRequestException("Invalid device id.", nameof(req.DeviceIds)));
            await updates.SetCanaryAsync(id, ids, user.Username ?? "unknown", ct);
            return Results.Ok(await updates.GetRolloutAsync(id, ct));
        });
        rollouts.MapPost("{id:guid}/rollout", async (Guid id, IAgentUpdateService updates, CancellationToken ct) =>
        {
            await updates.PromoteAsync(id, ct);
            return Results.Ok(await updates.GetRolloutAsync(id, ct));
        });
        rollouts.MapPost("{id:guid}/pause", async (Guid id, IAgentUpdateService updates, CancellationToken ct) =>
        {
            await updates.PauseAsync(id, ct);
            return Results.Ok(await updates.GetRolloutAsync(id, ct));
        });
        rollouts.MapPost("{id:guid}/rollback", async (Guid id, IAgentUpdateService updates, CancellationToken ct) =>
        {
            await updates.RollbackAsync(id, ct);
            return Results.Ok(await updates.GetRolloutAsync(id, ct));
        });

        var sa = api.MapGroup("/agent-packages").RequireAuthorization(AuthDefaults.SuperAdminPolicy);

        // Multipart upload: a single "package" zip that contains manifest.json plus the files it lists.
        // Read via the raw form (not IFormFile binding) so minimal-API antiforgery does not apply to a bearer-token API.
        sa.MapPost("", async (HttpRequest http, IAgentPackageService packages, CancellationToken ct) =>
            {
                if (!http.HasFormContentType) return Results.BadRequest("Expected a multipart form upload.");
                var form = await http.ReadFormAsync(ct);
                var file = form.Files.GetFile("package");
                if (file is null || file.Length == 0) return Results.BadRequest("Missing 'package' file.");
                if (file.Length > MaxPackageBytes) return Results.BadRequest("Package too large.");

                using var ms = new MemoryStream((int)file.Length);
                await file.CopyToAsync(ms, ct);
                var dto = await packages.UploadAsync(ms.ToArray(), ct);
                return Results.Created($"/api/v1/agent-packages/{dto.Version}", dto);
            })
            .WithMetadata(new RequestSizeLimitAttribute(MaxPackageBytes + 1024 * 1024))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxPackageBytes + 1024 * 1024 })
            .DisableAntiforgery();
    }
}

/// <summary>Server-wide cap on simultaneous package streams (singleton; sized from <see cref="AgentUpdateOptions.MaxConcurrentDownloads"/>).</summary>
public sealed class PackageDownloadGate(IOptions<AgentUpdateOptions> options)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(options.Value.MaxConcurrentDownloads, 1));
    public bool TryEnter() => _slots.Wait(0);
    public void Exit() => _slots.Release();
}
