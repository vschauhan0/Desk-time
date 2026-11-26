using System.Security.Claims;
using NISS.Api.Http;
using NISS.Api.Security;
using NISS.Application.Screenshots;

namespace NISS.Api.Features;

/// <summary>Screenshot ingestion (device) and, later, timeline reads + live view (SUPERADMIN).</summary>
public static class ScreenshotEndpoints
{
    /// <summary>Max accepted screenshot upload size (bytes).</summary>
    private const int MaxUploadBytes = 5 * 1024 * 1024;

    public static void MapScreenshots(this RouteGroupBuilder api)
    {
        // Device-authenticated periodic screenshot upload: raw JPEG body + metadata in the query string.
        api.MapPost("/devices/screenshots", async (
                [AsParameters] ScreenshotMeta meta,
                ClaimsPrincipal user,
                HttpRequest http,
                IScreenshotIngestionService ingestion,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(meta.FrameId)) return Results.BadRequest("frameId is required.");
                if (http.ContentLength is > MaxUploadBytes) return Results.BadRequest("Screenshot too large.");

                using var ms = new MemoryStream();
                await http.Body.CopyToAsync(ms, ct);
                var jpeg = ms.ToArray();
                if (jpeg.Length == 0) return Results.BadRequest("Empty screenshot body.");
                if (jpeg.Length > MaxUploadBytes) return Results.BadRequest("Screenshot too large.");

                return Results.Ok(await ingestion.IngestAsync(user.GetDeviceId(), meta, jpeg, ct));
            })
            .RequireAuthorization(AuthDefaults.DevicePolicy)
            .RequireRateLimiting(RateLimiting.Ingest);
    }
}
