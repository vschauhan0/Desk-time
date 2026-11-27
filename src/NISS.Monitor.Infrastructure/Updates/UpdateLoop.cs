namespace NISS.Monitor.Infrastructure.Updates;

/// <summary>
/// The watchdog's update loop. Every step is guarded: an exception from the startup resume or from any tick is logged
/// and the loop carries on, so a failing update can never take down the host (and with it the anti-tamper watchdog).
/// Only cancellation ends the loop.
/// </summary>
public sealed class UpdateLoop(Func<CancellationToken, Task> resumeAtStart, Func<CancellationToken, Task> tick,
    TimeSpan interval, TimeProvider time, Action<string, Exception> onError)
{
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await GuardedAsync("startup resume", resumeAtStart, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(interval, time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await GuardedAsync("update check", tick, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task GuardedAsync(string what, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        try { await step(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { onError(what, ex); }
    }
}
