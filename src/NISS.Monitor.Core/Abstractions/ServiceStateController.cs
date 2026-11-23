namespace NISS.Monitor.Core.Abstractions;

/// <summary>
/// Minimal, testable view of "is this named Windows service running, and can I start it". Kept separate from
/// <c>System.ServiceProcess</c> so the watchdog decision logic (<see cref="Engines.PeerServiceWatchdog"/>) can be
/// unit tested without a real Windows service or an elevated process.
/// </summary>
public interface IServiceStateController
{
    /// <summary>True when the named service currently reports the Running state. False (never throws) if it does not exist or cannot be queried.</summary>
    bool IsRunning(string serviceName);

    /// <summary>Start the named service if it is stopped. A no-op if it is already running, starting, or does not exist - never throws.</summary>
    void StartIfStopped(string serviceName);
}
