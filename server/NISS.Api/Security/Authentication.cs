using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NISS.Application.Abstractions;
using NISS.Application.Devices;
using NISS.Infrastructure.Security;
using NISS.Monitor.Protocol;

namespace NISS.Api.Security;

/// <summary>Scheme and claim names used by the API.</summary>
public static class AuthDefaults
{
    public const string DeviceScheme = "NissDevice";
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string DeviceIdClaim = "device_guid";
    public const string DeviceCodeClaim = "device_code";
    public const string RoleClaim = "role";
    public const string NameClaim = "name";
    public const string SubjectClaim = "sub";

    public const string ViewerPolicy = "Viewer";
    public const string AdminPolicy = "Admin";
    public const string SuperAdminPolicy = "SuperAdmin";
    public const string DevicePolicy = "Device";
}

/// <summary>
/// Authenticates agents: <c>Authorization: NissDevice &lt;deviceId&gt;:&lt;secret&gt;</c>. A disabled or revoked device still
/// authenticates (so a heartbeat can report its status); the endpoints decide what it may do.
/// </summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header)) return AuthenticateResult.NoResult();
        var value = header.ToString();
        var prefix = AuthDefaults.DeviceScheme + " ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var credentials = value[prefix.Length..].Trim();
        var split = credentials.IndexOf(':');
        if (split <= 0 || split == credentials.Length - 1 || credentials.Length > 256) return AuthenticateResult.Fail("Malformed device credentials.");

        var authenticator = Context.RequestServices.GetRequiredService<IDeviceAuthenticator>();
        var result = await authenticator.AuthenticateAsync(credentials[..split], credentials[(split + 1)..], Context.RequestAborted);
        if (!result.Success) return AuthenticateResult.Fail("Invalid device credentials.");

        var identity = new ClaimsIdentity(
        [
            new Claim(AuthDefaults.DeviceIdClaim, result.DeviceId.ToString()),
            new Claim(AuthDefaults.DeviceCodeClaim, result.DeviceCode),
            new Claim(ClaimTypes.NameIdentifier, result.DeviceCode),
        ], AuthDefaults.DeviceScheme, ClaimTypes.NameIdentifier, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthDefaults.DeviceScheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = AuthDefaults.DeviceScheme;
        return base.HandleChallengeAsync(properties);
    }
}

/// <summary>JWT bearer validation using the signing key held by <see cref="ISigningKeyProvider"/>.</summary>
public sealed class JwtBearerSetup(ISigningKeyProvider keys, TimeProvider clock) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = AccessTokenIssuer.Issuer,
            ValidateAudience = true, ValidAudience = AccessTokenIssuer.Audience,
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            // Use the injected clock so token lifetime and the rest of the server agree (and tests can control time).
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore.Value <= now + parameters.ClockSkew) && expires is not null && expires.Value > now - parameters.ClockSkew;
            },
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKeyResolver = (_, _, _, _) => [new SymmetricSecurityKey(keys.GetKey())],
            NameClaimType = AuthDefaults.NameClaim, RoleClaimType = AuthDefaults.RoleClaim,
        };
        options.Events = new JwtBearerEvents
        {
            // Browsers cannot set headers on WebSocket upgrades: SignalR passes the JWT in the query string.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(HubMethods.Route))
                    context.Token = token;
                return Task.CompletedTask;
            },
        };
    }
}

/// <summary>The dashboard user of the current HTTP request.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(AuthDefaults.SubjectClaim), out var id) ? id : null;
    public string? Username => Principal?.FindFirstValue(AuthDefaults.NameClaim);
    public string? Role => Principal?.FindFirstValue(AuthDefaults.RoleClaim);
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Helpers for reading the authenticated device.</summary>
public static class DevicePrincipalExtensions
{
    public static Guid GetDeviceId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AuthDefaults.DeviceIdClaim), out var id) ? id : throw new InvalidOperationException("Not a device principal.");

    public static string? GetDeviceCode(this ClaimsPrincipal user) => user.FindFirstValue(AuthDefaults.DeviceCodeClaim);
}
