using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NISS.Api.Security;
using NISS.Application.Updates;

namespace NISS.Api.Features;

/// <summary>Agent update packages: signed-package upload (SUPERADMIN).</summary>
public static class AgentUpdateEndpoints
{
    /// <summary>Max accepted package size (bytes).</summary>
    private const long MaxPackageBytes = 200L * 1024 * 1024;

    public static void MapAgentUpdates(this RouteGroupBuilder api)
    {
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
