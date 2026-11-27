using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Updates;

/// <summary>An opened update package: its manifest plus a reader for the files it contains.</summary>
public sealed record UpdatePackage(ReleaseManifest Manifest, Func<string, byte[]> ReadFile);

/// <summary>
/// Every real Windows operation the update orchestrator drives. The orchestrator is pure logic; the implementation
/// (download, unzip, side-by-side copy, sc.exe switch, health probe) lives behind this seam.
/// </summary>
public interface IUpdateHost
{
    Task<byte[]> DownloadAsync(string url, CancellationToken ct);

    /// <summary>Parses the package bytes (e.g. unzip + manifest.json). Throws if the package is short, partial or corrupt.</summary>
    UpdatePackage OpenPackage(byte[] package);

    /// <summary>Lays the already-verified package down next to the running version. Must not switch the service.</summary>
    Task InstallSideBySideAsync(string version, byte[] package, CancellationToken ct);

    Task SwitchToAsync(string version);

    Task<bool> HealthyAsync(string version, TimeSpan window, CancellationToken ct);

    Task RollbackAsync(string previousVersion);

    Task ReportAsync(UpdateStatusReport report);
}
