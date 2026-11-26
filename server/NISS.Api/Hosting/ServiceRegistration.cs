using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using NISS.Api.Features;
using NISS.Api.Http;
using NISS.Api.Realtime;
using NISS.Api.Security;
using NISS.Application;
using NISS.Application.Abstractions;
using NISS.Application.Validation;
using NISS.Infrastructure;
using NISS.Monitor.Protocol;
using NISS.Workers;
using Serilog;

namespace NISS.Api.Hosting;

/// <summary>Composition root: service registration, middleware pipeline and endpoint mapping.</summary>
public static class ServiceRegistration
{
    public static void AddNissServices(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddNissOptions(builder.Configuration);
        services.AddNissApplication();
        services.AddNissInfrastructure();
        services.AddNissWorkers();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
        services.AddSingleton<Realtime.ILiveRelay, Realtime.LiveRelay>();
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();

        // One JSON wire format everywhere (camelCase, string enums, nulls omitted) - identical to NISS.Monitor.Protocol.
        services.ConfigureHttpJsonOptions(o => Apply(o.SerializerOptions));
        services.AddSignalR().AddJsonProtocol(o => Apply(o.PayloadSerializerOptions));

        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemExceptionHandler>();

        services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = AuthDefaults.BearerScheme;
                o.DefaultChallengeScheme = AuthDefaults.BearerScheme;
            })
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(AuthDefaults.DeviceScheme, _ => { });
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerSetup>();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthDefaults.ViewerPolicy, p => p.AddAuthenticationSchemes(AuthDefaults.BearerScheme).RequireAuthenticatedUser()
                .RequireRole(Roles.Viewer, Roles.Admin, Roles.SuperAdmin))
            .AddPolicy(AuthDefaults.AdminPolicy, p => p.AddAuthenticationSchemes(AuthDefaults.BearerScheme).RequireAuthenticatedUser()
                .RequireRole(Roles.Admin, Roles.SuperAdmin))
            .AddPolicy(AuthDefaults.SuperAdminPolicy, p => p.AddAuthenticationSchemes(AuthDefaults.BearerScheme).RequireAuthenticatedUser()
                .RequireRole(Roles.SuperAdmin))
            .AddPolicy(AuthDefaults.DevicePolicy, p => p.AddAuthenticationSchemes(AuthDefaults.DeviceScheme).RequireAuthenticatedUser());

        services.AddNissCors();
        services.AddNissRateLimiting();
    }

    public static void UseNissPipeline(this WebApplication app)
    {
        // Request logging is outermost so it sees the final status code (the exception handler has already turned exceptions into 4xx/5xx).
        // It logs method, path (never the query string, which can carry the SignalR access_token) and status - no headers, no bodies.
        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} in {Elapsed:0.0} ms";
            o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
                : ctx.Response.StatusCode is 401 or 403 or 429 ? Serilog.Events.LogEventLevel.Warning
                : ctx.Request.Path.StartsWithSegments("/api/v1/health") ? Serilog.Events.LogEventLevel.Debug
                : Serilog.Events.LogEventLevel.Information;
        });
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
        app.UseWebSockets();
        app.UseRouting();
        app.UseCors(CorsConfiguration.PolicyName);   // after routing (endpoint metadata), before authentication so 401/403 carry CORS headers
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
    }

    public static void MapNissEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapHealth();
        api.MapAuth();
        api.MapAdminUsers();
        api.MapAgent();
        api.MapEnrollmentTokens();
        api.MapDevices();
        api.MapScreenshots();
        api.MapLiveSocket();
        api.MapEmployees();
        api.MapEventLists();
        api.MapReports();
        api.MapSettings();
        app.MapHub<MonitorHub>(HubMethods.Route).RequireCors(CorsConfiguration.PolicyName);
    }

    private static void Apply(JsonSerializerOptions target)
    {
        var shared = ProtocolJson.Options;
        target.PropertyNamingPolicy = shared.PropertyNamingPolicy;
        target.PropertyNameCaseInsensitive = true;
        target.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        if (!target.Converters.OfType<JsonStringEnumConverter>().Any()) target.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }
}
