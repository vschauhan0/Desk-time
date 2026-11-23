using System.ServiceProcess;
using NISS.Monitor.Core.Abstractions;

namespace NISS.Monitor.Infrastructure.Setup;

/// <summary>Real Windows implementation of <see cref="IServiceStateController"/> via the standard Service Control Manager APIs.</summary>
public sealed class WindowsServiceStateController : IServiceStateController
{
    /// <inheritdoc />
    public bool IsRunning(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // not installed / cannot query: treat as "not running", nothing more we can do here
        }
    }

    /// <inheritdoc />
    public void StartIfStopped(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status is ServiceControllerStatus.Stopped) sc.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not installed, already starting, or transient SCM error: the next poll tries again.
        }
    }
}
