using System.Security.Cryptography;
using System.Text;
using NISS.Monitor.Core.Updates;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Tests;

public sealed class UpdateOrchestratorTests
{
    private static readonly PublisherPin Pin = new(["CN=Test"]);

    private sealed class FakeSig(bool ok = true) : ISignatureChecker
    {
        public bool IsAuthenticodeSignedBy(byte[] fileBytes, PublisherPin pin) => ok;
    }

    private sealed class FakeHost : IUpdateHost
    {
        public List<string> Calls { get; } = [];
        public List<UpdateStatusReport> Reports { get; } = [];
        public byte[] Downloaded = [1, 2, 3];
        public Func<byte[], UpdatePackage> Open = _ => throw new InvalidOperationException("no package");
        public bool Healthy = true;

        public Task<byte[]> DownloadAsync(string url, CancellationToken ct) { Calls.Add("download"); return Task.FromResult(Downloaded); }
        public UpdatePackage OpenPackage(byte[] package) { Calls.Add("open"); return Open(package); }
        public Task InstallSideBySideAsync(string version, byte[] package, CancellationToken ct) { Calls.Add("install"); return Task.CompletedTask; }
        public Task SwitchToAsync(string version) { Calls.Add("switch"); return Task.CompletedTask; }
        public Task<bool> HealthyAsync(string version, TimeSpan window, CancellationToken ct) { Calls.Add("health"); return Task.FromResult(Healthy); }
        public Task RollbackAsync(string previousVersion) { Calls.Add("rollback:" + previousVersion); return Task.CompletedTask; }
        public Task ReportAsync(UpdateStatusReport r) { Reports.Add(r); return Task.CompletedTask; }
    }

    private static UpdatePackage Package(string version, byte[] content, string? shaOverride = null)
    {
        var sha = shaOverride ?? Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var manifest = new ReleaseManifest(version, null, [new ManifestFile("agent.exe", sha)], false, null);
        return new UpdatePackage(manifest, _ => content);
    }

    private static (FakeHost host, UpdateOrchestrator orch) Make(UpdatePackage pkg, bool sigOk = true)
    {
        var host = new FakeHost { Open = _ => pkg };
        return (host, new UpdateOrchestrator(host, new FakeSig(sigOk), Pin));
    }

    [Fact]
    public async Task Happy_path_installs_switches_and_reports_updated()
    {
        var (host, orch) = Make(Package("2.0.0", Encoding.UTF8.GetBytes("new")));
        await orch.RunAsync("2.0.0", "http://x/p.zip", "1.0.0", CancellationToken.None);

        Assert.Equal(["download", "open", "install", "switch", "health"], host.Calls);
        Assert.Equal(["downloading", "verifying", "installing", "health_check", "updated"], host.Reports.Select(r => r.Phase));
    }

    [Fact]
    public async Task Verify_failure_aborts_without_install_or_switch()
    {
        var (host, orch) = Make(Package("2.0.0", [9]), sigOk: false);
        await orch.RunAsync("2.0.0", "http://x/p.zip", "1.0.0", CancellationToken.None);

        Assert.DoesNotContain("install", host.Calls);
        Assert.DoesNotContain("switch", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("rollback"));
        var last = host.Reports[^1];
        Assert.Equal("failed", last.Phase);
        Assert.False(string.IsNullOrEmpty(last.Error));
    }

    [Fact]
    public async Task Unhealthy_new_version_rolls_back_and_reports_rolled_back()
    {
        var (host, orch) = Make(Package("2.0.0", [1, 2]));
        host.Healthy = false;
        await orch.RunAsync("2.0.0", "http://x/p.zip", "1.0.0", CancellationToken.None);

        Assert.Contains("rollback:1.0.0", host.Calls);
        Assert.Equal("rolled_back", host.Reports[^1].Phase);
    }

    [Fact]
    public async Task Corrupt_or_partial_download_fails_verification_with_no_install()
    {
        // Manifest promises one hash but the bytes that arrived are short/truncated.
        var (host, orch) = Make(Package("2.0.0", [1], shaOverride: new string('a', 64)));
        await orch.RunAsync("2.0.0", "http://x/p.zip", "1.0.0", CancellationToken.None);
        Assert.DoesNotContain("install", host.Calls);
        Assert.Equal("failed", host.Reports[^1].Phase);

        // A package the host cannot even open (truncated zip) also aborts before install.
        var host2 = new FakeHost { Open = _ => throw new InvalidDataException("truncated") };
        await new UpdateOrchestrator(host2, new FakeSig(), Pin).RunAsync("2.0.0", "u", "1.0.0", CancellationToken.None);
        Assert.DoesNotContain("install", host2.Calls);
        Assert.DoesNotContain("switch", host2.Calls);
        Assert.Equal("failed", host2.Reports[^1].Phase);
    }

    [Fact]
    public async Task Manifest_version_not_matching_target_is_rejected()
    {
        var (host, orch) = Make(Package("3.0.0", [1]));
        await orch.RunAsync("2.0.0", "u", "1.0.0", CancellationToken.None);
        Assert.DoesNotContain("install", host.Calls);
        Assert.Equal("failed", host.Reports[^1].Phase);
    }
}
