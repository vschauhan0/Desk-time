using NISS.Monitor.Protocol;

namespace NISS.Monitor.Core.Updates;

/// <summary>
/// Drives one update attempt: download -> verify -> install side-by-side -> switch -> health gate -> rollback on failure.
/// Nothing touches disk or the service until <see cref="PackageVerification.Verify"/> passes: the only path to
/// <c>InstallSideBySideAsync</c> runs through a successful verification of the very bytes being installed.
/// </summary>
public sealed class UpdateOrchestrator(IUpdateHost host, ISignatureChecker sig, PublisherPin pin)
{
    public static readonly TimeSpan HealthWindow = TimeSpan.FromSeconds(90);
    private const int Attempt = 1;

    public async Task RunAsync(string targetVersion, string targetUrl, string currentVersion, CancellationToken ct)
    {
        byte[] package;
        try
        {
            await Report(targetVersion, "downloading", null);
            package = await host.DownloadAsync(targetUrl, ct);

            await Report(targetVersion, "verifying", null);
            var opened = host.OpenPackage(package);
            if (!string.Equals(opened.Manifest.Version, targetVersion, StringComparison.Ordinal))
            {
                await Report(targetVersion, "failed",
                    $"Package version {opened.Manifest.Version} does not match requested {targetVersion}.");
                return;
            }
            var result = PackageVerification.Verify(opened.Manifest, opened.ReadFile, sig, pin, currentVersion);
            if (!result.Ok)
            {
                await Report(targetVersion, "failed", result.Error);
                return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Download or unreadable/corrupt package: nothing has been installed or switched yet.
            await Report(targetVersion, "failed", ex.Message);
            return;
        }

        // Verified from here on; failures below roll back to the previous version.
        try
        {
            await Report(targetVersion, "installing", null);
            await host.InstallSideBySideAsync(targetVersion, package, ct);
            await host.SwitchToAsync(targetVersion);

            await Report(targetVersion, "health_check", null);
            if (await host.HealthyAsync(targetVersion, HealthWindow, ct))
            {
                await Report(targetVersion, "updated", null);
                return;
            }
            await RollBack(targetVersion, currentVersion, "New version failed its health check.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await RollBack(targetVersion, currentVersion, ex.Message);
        }
    }

    private async Task RollBack(string targetVersion, string currentVersion, string reason)
    {
        try
        {
            await host.RollbackAsync(currentVersion);
            await Report(targetVersion, "rolled_back", reason);
        }
        catch (Exception ex)
        {
            await Report(targetVersion, "failed", $"{reason} Rollback also failed: {ex.Message}");
        }
    }

    private Task Report(string version, string phase, string? error) =>
        host.ReportAsync(new UpdateStatusReport(version, phase, error, Attempt));
}
