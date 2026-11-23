using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NISS.Api.Hosting;
using NISS.Api.Security;
using NISS.Application.Common;
using NISS.Application.Options;

namespace NISS.Api.Http;

/// <summary>Maps exceptions to RFC 7807 problem details. Details of unexpected errors are logged, never returned.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case AppException app when app.Errors is not null:
                problem = new ValidationProblemDetails(app.Errors) { Status = app.StatusCode, Title = "One or more validation errors occurred." };
                break;
            case AppException app:
                problem = new ProblemDetails { Status = app.StatusCode, Title = app.Title, Detail = app.Detail };
                break;
            case BadHttpRequestException bad:
                problem = new ProblemDetails { Status = bad.StatusCode, Title = bad.StatusCode == 413 ? "Request body too large." : "Bad request." };
                if (bad.StatusCode == 400) problem.Detail = "The request could not be read (malformed body or parameters).";
                break;
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true;   // the client went away
            default:
                log.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path.Value);
                problem = new ProblemDetails { Status = 500, Title = "An unexpected error occurred." };
                break;
        }

        problem.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = problem.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }
}

/// <summary>Defensive response headers for an API.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Cross-Origin-Resource-Policy"] = "same-site";
            if (!h.ContainsKey("Cache-Control")) h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>Runs the FluentValidation validator of the request DTO and answers 400 with an <c>errors</c> map.</summary>
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var body = context.Arguments.OfType<T>().FirstOrDefault();
        if (body is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A JSON request body is required."] });

        var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is not null)
        {
            var result = await validator.ValidateAsync(body, context.HttpContext.RequestAborted);
            if (!result.IsValid)
            {
                var errors = result.Errors.GroupBy(e => CamelCase(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
                return Results.ValidationProblem(errors);
            }
        }
        return await next(context);
    }

    private static string CamelCase(string name) =>
        string.Join('.', name.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}

/// <summary>Endpoint extension methods.</summary>
public static class EndpointExtensions
{
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>();
}

/// <summary>CORS: the dashboard is served on <c>Dashboard:Port</c> of any host, so any origin with that port is allowed (with credentials).</summary>
public static class CorsConfiguration
{
    public const string PolicyName = "Dashboard";

    public static void AddNissCors(this IServiceCollection services)
    {
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<DashboardOptions>>((cors, dashboard) =>
            cors.AddPolicy(PolicyName, policy => policy
                .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                                              && uri.Port == dashboard.Value.Port && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
    }
}

/// <summary>Rate limiting: login 5/min/IP, enroll 10/min/IP, ingest generous per device (values configurable under <c>RateLimits</c>).</summary>
public static class RateLimiting
{
    public const string Login = "login";
    public const string Refresh = "refresh";
    public const string Enroll = "enroll";
    public const string Ingest = "ingest";

    public static void AddNissRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((limiter, limits) =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(Login, ctx => Fixed("login:" + Ip(ctx), limits.Value.LoginPerMinute));
            limiter.AddPolicy(Refresh, ctx => Fixed("refresh:" + Ip(ctx), limits.Value.RefreshPerMinute));
            limiter.AddPolicy(Enroll, ctx => Fixed("enroll:" + Ip(ctx), limits.Value.EnrollPerMinute));
            limiter.AddPolicy(Ingest, ctx => Fixed("ingest:" + (ctx.User.GetDeviceCode() ?? Ip(ctx)), limits.Value.IngestPerMinute));
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
        });
    }

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Fixed(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
        });
}
