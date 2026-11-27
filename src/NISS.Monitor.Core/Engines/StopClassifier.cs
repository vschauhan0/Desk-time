namespace NISS.Monitor.Core.Engines;

/// <summary>How a service stop should be reported. <see cref="InteractiveUser"/> is non-null only for a deliberate
/// stop (someone stopping the service while signed in) - a benign OS shutdown leaves it null so it never raises a
/// tamper alert, and an older server simply ignores the field.</summary>
public readonly record struct StopReport(string Reason, string? InteractiveUser);

/// <summary>
/// Pure rule separating a benign OS shutdown from a deliberate service stop, and attributing the latter to the
/// signed-in user. Isolated from Win32 so the false-positive policy is unit-testable: the real caller supplies
/// <c>isShuttingDown</c> (GetSystemMetrics SM_SHUTTINGDOWN) and <c>consoleUser</c> (the active WTS session user).
/// </summary>
public static class StopClassifier
{
    public const string Shutdown = "SHUTDOWN";
    public const string ServiceStop = "SERVICE_STOP";

    public static StopReport Classify(bool isShuttingDown, string? consoleUser)
    {
        if (isShuttingDown) return new StopReport(Shutdown, null);
        var user = string.IsNullOrWhiteSpace(consoleUser) ? null : consoleUser.Trim();
        return new StopReport(ServiceStop, user);
    }
}
