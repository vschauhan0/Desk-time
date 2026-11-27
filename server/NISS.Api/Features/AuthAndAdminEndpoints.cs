using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NISS.Api.Http;
using NISS.Api.Security;
using NISS.Application.Abstractions;
using NISS.Application.Auth;
using NISS.Monitor.Protocol;

namespace NISS.Api.Features;

/// <summary>GET /health (anonymous).</summary>
public static class HealthEndpoints
{
    public static void MapHealth(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (IAppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var database = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                database = await db.Database.CanConnectAsync(timeout.Token);
            }
            catch (Exception) { /* reported as database=false */ }

            var version = typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
            var body = new HealthResponse(database ? "Healthy" : "Degraded", version, clock.GetUtcNow(), database);
            return database ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous().WithName("Health");
    }
}

/// <summary>POST /auth/login, /auth/refresh, /auth/logout, GET /auth/me.</summary>
public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (LoginRequest request, IAuthService auth, CancellationToken ct) => Results.Ok(await auth.LoginAsync(request, ct)))
            .AllowAnonymous().RequireRateLimiting(RateLimiting.Login).WithValidation<LoginRequest>();

        g.MapPost("/refresh", async (RefreshRequest request, IAuthService auth, CancellationToken ct) => Results.Ok(await auth.RefreshAsync(request, ct)))
            .AllowAnonymous().RequireRateLimiting(RateLimiting.Refresh).WithValidation<RefreshRequest>();

        g.MapPost("/logout", async (RefreshRequest? request, IAuthService auth, CancellationToken ct) =>
        {
            await auth.LogoutAsync(request?.RefreshToken, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthDefaults.ViewerPolicy);

        g.MapGet("/me", async (ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
        {
            var id = Guid.Parse(user.FindFirstValue(AuthDefaults.SubjectClaim)!);
            return Results.Ok(await auth.MeAsync(id, ct));
        }).RequireAuthorization(AuthDefaults.ViewerPolicy);
    }
}

/// <summary>/admin-users (SUPERADMIN).</summary>
public static class AdminUserEndpoints
{
    public static void MapAdminUsers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/admin-users").RequireAuthorization(AuthDefaults.SuperAdminPolicy);

        // The catalogue of pages access can be granted on, for the admin-user editor.
        g.MapGet("/pages", () => Results.Ok(AppPages.All));

        g.MapGet("", async (IAdminUserService users, CancellationToken ct) => Results.Ok(await users.ListAsync(ct)));

        g.MapPost("", async (AdminUserUpsert body, IAdminUserService users, CancellationToken ct) =>
        {
            var created = await users.CreateAsync(body, ct);
            return Results.Created($"/api/v1/admin-users/{created.Id}", created);
        }).WithValidation<AdminUserUpsert>();

        g.MapPut("/{id:guid}", async (Guid id, AdminUserUpsert body, IAdminUserService users, CancellationToken ct) =>
            Results.Ok(await users.UpdateAsync(id, body, ct))).WithValidation<AdminUserUpsert>();

        g.MapDelete("/{id:guid}", async (Guid id, IAdminUserService users, CancellationToken ct) =>
        {
            await users.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }
}
