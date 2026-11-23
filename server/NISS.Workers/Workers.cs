using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Application.Health;
using NISS.Application.Options;
using NISS.Application.Retention;

namespace NISS.Workers;

/// <summary>Shared loop: run a job in a fresh scope, log failures, never let an exception stop the worker.</summary>
public abstract class ScopedWorker(IServiceScopeFactory scopes, ILogger logger) : BackgroundService
{
    protected async Task RunJobAsync<TService>(Func<TService, CancellationToken, Task> job, string name, CancellationToken ct) where TService : notnull
    {
        try
        {
            using var scope = scopes.CreateScope();
            await job(scope.ServiceProvider.GetRequiredService<TService>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background job {Job} failed; it will run again on its next cycle", name);
        }
    }
}

/// <summary>Re-evaluates every device's health every 15 seconds (interval configurable) and publishes transitions.</summary>
public sealed class DeviceHealthWorker(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<DeviceHealthWorker> logger)
    : ScopedWorker(scopes, logger)
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.HealthIntervalSeconds));
        logger.LogInformation("Device health worker started (every {Seconds}s)", interval.TotalSeconds);
        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            await RunJobAsync<IDeviceHealthService>((s, ct) => s.RunOnceAsync(ct), "device-health", stoppingToken);
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>Prunes old data once a day at <c>Workers:RetentionHourLocal</c> (server local time).</summary>
public sealed class RetentionWorker(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<RetentionWorker> logger)
    : ScopedWorker(scopes, logger)
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(clock.GetUtcNow(), clock.LocalTimeZone, options.Value.RetentionHourLocal);
            logger.LogInformation("Next retention run in {Delay}", delay);
            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
            await RunJobAsync<IRetentionService>((s, ct) => s.RunAsync(ct), "retention", stoppingToken);
        }
    }

    /// <summary>Time from <paramref name="now"/> until the next occurrence of the given local hour.</summary>
    public static TimeSpan DelayUntilNextRun(DateTimeOffset now, TimeZoneInfo zone, int hourLocal)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var next = new DateTimeOffset(local.Year, local.Month, local.Day, Math.Clamp(hourLocal, 0, 23), 0, 0, local.Offset);
        if (next <= local) next = next.AddDays(1);
        return next - local;
    }
}

/// <summary>Deletes stale enrollment tokens periodically.</summary>
public sealed class EnrollmentTokenCleanupWorker(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<EnrollmentTokenCleanupWorker> logger)
    : ScopedWorker(scopes, logger)
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.TokenCleanupIntervalMinutes)), clock);
        do
        {
            await RunJobAsync<IEnrollmentTokenCleanupService>((s, ct) => s.RunAsync(ct), "enrollment-token-cleanup", stoppingToken);
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>Registers the hosted workers (each worker exits immediately when <c>Workers:Enabled</c> is false).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNissWorkers(this IServiceCollection services)
    {
        services.AddHostedService<DeviceHealthWorker>();
        services.AddHostedService<RetentionWorker>();
        services.AddHostedService<EnrollmentTokenCleanupWorker>();
        return services;
    }
}
