using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using NISS.Monitor.Core.Updates;
using NISS.Monitor.Infrastructure.Updates;
using NISS.Monitor.Protocol;
using NISS.Monitor.Setup;

namespace NISS.Monitor.Tests;

public sealed class WindowsUpdateHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "niss-upd-" + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "Monitor");

    public WindowsUpdateHostTests() => Directory.CreateDirectory(Install);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class FakeSvc : IUpdateServiceControl
    {
        public List<string> Calls { get; } = [];
        public bool Running = true;
        public void SwitchBinPath(string mainExePath) => Calls.Add("bin:" + mainExePath);
        public void RestartMainService() => Calls.Add("restart");
        public bool IsMainRunning() => Running;
        public void SetSessionRunKey(string versionDir) => Calls.Add("run:" + versionDir);
    }

    private (WindowsUpdateHost host, FakeSvc svc) Host(string legacy = "1.0.0")
    {
        var svc = new FakeSvc();
        var opts = new UpdateHostOptions
        {
            InstallDir = Install, StatePath = Path.Combine(_root, "update-state.json"), LegacyVersion = legacy,
            ServerBaseUrl = new Uri("https://server"), Auth = () => null, ReadHealth = () => (null, null),
        };
        return (new WindowsUpdateHost(opts, new HttpClient(), svc, _ => Task.CompletedTask), svc);
    }

    private static byte[] Zip(IEnumerable<(string name, byte[] data)> entries, ReleaseManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string n, byte[] d) { using var s = z.CreateEntry(n).Open(); s.Write(d); }
            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJson.Options));
            foreach (var (n, d) in entries) Add(n, d);
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // ---- layout / command building ----

    [Fact]
    public void Version_folder_and_binPath_command_are_built_correctly()
    {
        var dir = UpdateLayout.VersionDir(@"C:\Program Files\NISS\Monitor", "2.1.0");
        Assert.Equal(@"C:\Program Files\NISS\Monitor\app\2.1.0", dir);
        var exe = Path.Combine(dir, "Windows.RunService.exe");
        Assert.Equal(@"config NISSMonitorService binPath= ""C:\Program Files\NISS\Monitor\app\2.1.0\Windows.RunService.exe""",
            AgentSetup.ScConfigBinPathArgs("NISSMonitorService", exe));
    }

    [Theory]
    [InlineData("..")] [InlineData("1.0.0\\..\\x")] [InlineData("1.0/../0")] [InlineData("abc")] [InlineData("")]
    public void Version_folder_rejects_non_version_strings(string v) =>
        Assert.Throws<ArgumentException>(() => UpdateLayout.VersionDir(Install, v));

    [Fact]
    public void Pruning_keeps_current_and_previous_only()
    {
        var prune = UpdateLayout.VersionsToPrune(["1.0.0", "1.1.0", "1.2.0", "1.3.0"], "1.3.0", "1.2.0");
        Assert.Equal(["1.0.0", "1.1.0"], prune);
    }

    [Fact]
    public void Prune_deletes_old_folders_on_disk_and_keeps_previous()
    {
        var (host, _) = Host();
        foreach (var v in new[] { "1.1.0", "1.2.0", "1.3.0" }) Directory.CreateDirectory(UpdateLayout.VersionDir(Install, v));
        host.Prune("1.3.0", "1.2.0");
        Assert.False(Directory.Exists(UpdateLayout.VersionDir(Install, "1.1.0")));
        Assert.True(Directory.Exists(UpdateLayout.VersionDir(Install, "1.2.0")));
        Assert.True(Directory.Exists(UpdateLayout.VersionDir(Install, "1.3.0")));
    }

    // ---- switch / rollback ----

    [Fact]
    public async Task Switch_repoints_service_to_new_version_folder_then_restarts_and_records_previous()
    {
        var (host, svc) = Host("1.0.0");
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        await host.SwitchToAsync("2.0.0");

        var dir = UpdateLayout.VersionDir(Install, "2.0.0");
        Assert.Equal([$"bin:{Path.Combine(dir, "Windows.RunService.exe")}", $"run:{dir}", "restart"], svc.Calls);
        Assert.Equal(new UpdateState("2.0.0", "1.0.0", "2.0.0"), host.State);
    }

    [Fact]
    public async Task Rollback_repoints_to_the_retained_previous_version_and_drops_the_failed_one()
    {
        var (host, svc) = Host("1.0.0");
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        await host.SwitchToAsync("2.0.0");
        svc.Calls.Clear();

        await host.RollbackAsync("1.0.0");   // legacy version lives in the install dir itself

        Assert.Equal([$"bin:{Path.Combine(Install, "Windows.RunService.exe")}", $"run:{Install}", "restart"], svc.Calls);
        Assert.Equal(new UpdateState("1.0.0", null, null), host.State);
        Assert.False(Directory.Exists(UpdateLayout.VersionDir(Install, "2.0.0")));
    }

    [Fact]
    public async Task Rollback_to_a_side_by_side_previous_uses_its_app_folder()
    {
        var (host, svc) = Host("1.0.0");
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "3.0.0"));
        await host.SwitchToAsync("2.0.0");
        await host.SwitchToAsync("3.0.0");
        svc.Calls.Clear();
        await host.RollbackAsync("2.0.0");
        Assert.Equal($"bin:{Path.Combine(UpdateLayout.VersionDir(Install, "2.0.0"), "Windows.RunService.exe")}", svc.Calls[0]);
    }

    [Fact]
    public async Task Rollback_without_the_previous_folder_fails_loudly()
    {
        var (host, _) = Host("1.0.0");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => host.RollbackAsync("0.9.0"));
    }

    // ---- zip-slip + manifest-only install ----

    [Theory]
    [InlineData("../evil.exe")] [InlineData("..\\evil.exe")] [InlineData("a/../../evil.exe")]
    [InlineData("C:\\evil.exe")] [InlineData("/evil.exe")] [InlineData("\\evil.exe")]
    public void SafeCombine_refuses_traversal_and_absolute_paths(string p) =>
        Assert.Null(UpdateLayout.SafeCombine(Path.Combine(_root, "t"), p));

    [Fact]
    public void SafeCombine_accepts_nested_relative_paths() =>
        Assert.NotNull(UpdateLayout.SafeCombine(Path.Combine(_root, "t"), "sub/dir/file.dll"));

    [Fact]
    public void OpenPackage_rejects_a_manifest_entry_with_dotdot()
    {
        var (host, _) = Host();
        var data = new byte[] { 1 };
        var zip = Zip([("ok.exe", data)], new ReleaseManifest("2.0.0", null, [new ManifestFile("../evil.exe", Sha(data))], false, null));
        Assert.Throws<InvalidDataException>(() => host.OpenPackage(zip));
    }

    [Fact]
    public void OpenPackage_rejects_an_absolute_manifest_path()
    {
        var (host, _) = Host();
        var data = new byte[] { 1 };
        var zip = Zip([], new ReleaseManifest("2.0.0", null, [new ManifestFile("C:\\Windows\\evil.exe", Sha(data))], false, null));
        Assert.Throws<InvalidDataException>(() => host.OpenPackage(zip));
    }

    [Fact]
    public void OpenPackage_rejects_a_zip_without_manifest_or_a_corrupt_zip()
    {
        var (host, _) = Host();
        Assert.ThrowsAny<Exception>(() => host.OpenPackage([1, 2, 3]));
    }

    [Fact]
    public async Task Install_writes_only_manifest_listed_files()
    {
        var (host, _) = Host();
        var good = new byte[] { 1, 2, 3 };
        var extra = new byte[] { 9, 9 };
        var zip = Zip([("agent.exe", good), ("planted.exe", extra)],
            new ReleaseManifest("2.0.0", null, [new ManifestFile("agent.exe", Sha(good))], false, null));

        await host.InstallSideBySideAsync("2.0.0", zip, CancellationToken.None);

        var dir = UpdateLayout.VersionDir(Install, "2.0.0");
        Assert.True(File.Exists(Path.Combine(dir, "agent.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "planted.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "manifest.json")));
        Assert.False(Directory.Exists(dir + ".staging"));
    }

    [Fact]
    public async Task Install_refuses_a_listed_file_that_is_missing_from_the_zip_and_leaves_nothing_behind()
    {
        var (host, _) = Host();
        var data = new byte[] { 1 };
        var zip = Zip([], new ReleaseManifest("2.0.0", null, [new ManifestFile("agent.exe", Sha(data))], false, null));
        await Assert.ThrowsAsync<FileNotFoundException>(() => host.InstallSideBySideAsync("2.0.0", zip, CancellationToken.None));
        Assert.False(Directory.Exists(UpdateLayout.VersionDir(Install, "2.0.0")));
    }

    [Fact]
    public async Task Install_refuses_bytes_that_do_not_match_the_manifest_hash()
    {
        var (host, _) = Host();
        var zip = Zip([("agent.exe", new byte[] { 1 })], new ReleaseManifest("2.0.0", null, [new ManifestFile("agent.exe", Sha([2]))], false, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => host.InstallSideBySideAsync("2.0.0", zip, CancellationToken.None));
        Assert.False(Directory.Exists(UpdateLayout.VersionDir(Install, "2.0.0")));
    }

    [Fact]
    public async Task Install_refuses_a_package_whose_manifest_version_differs_from_the_requested_one()
    {
        var (host, _) = Host();
        var d = new byte[] { 1 };
        var zip = Zip([("agent.exe", d)], new ReleaseManifest("9.9.9", null, [new ManifestFile("agent.exe", Sha(d))], false, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InstallSideBySideAsync("2.0.0", zip, CancellationToken.None));
    }

    [Fact]
    public async Task Download_refuses_plain_http()
    {
        var (host, _) = Host();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DownloadAsync("http://server/api/v1/agent/update/2.0.0", CancellationToken.None));
    }

    // ---- health gate + pending resume ----

    [Fact]
    public async Task Health_gate_passes_only_when_the_new_version_heartbeats_and_then_clears_pending_and_prunes()
    {
        var svc = new FakeSvc();
        var opts = new UpdateHostOptions
        {
            InstallDir = Install, StatePath = Path.Combine(_root, "s.json"), LegacyVersion = "1.0.0", ServerBaseUrl = new Uri("https://s"),
            Auth = () => null, ReadHealth = () => ("2.0.0", DateTimeOffset.UtcNow.AddSeconds(5)), HealthPoll = TimeSpan.FromMilliseconds(10),
        };
        var host = new WindowsUpdateHost(opts, new HttpClient(), svc, _ => Task.CompletedTask);
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        await host.SwitchToAsync("2.0.0");

        Assert.True(await host.HealthyAsync("2.0.0", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Null(host.State.Pending);
    }

    [Fact]
    public async Task Health_gate_fails_when_the_service_is_down()
    {
        var (host, svc) = Host();
        svc.Running = false;
        Assert.False(await host.HealthyAsync("2.0.0", TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_during_health_wait_keeps_pending_so_the_next_start_can_resume_or_roll_back()
    {
        var (host, _) = Host();
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        await host.SwitchToAsync("2.0.0");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.HealthyAsync("2.0.0", TimeSpan.FromMinutes(1), cts.Token));
        Assert.Equal("2.0.0", host.State.Pending);
    }

    [Fact]
    public async Task Resume_pending_rolls_back_when_the_switch_is_never_confirmed()
    {
        var reports = new List<UpdateStatusReport>();
        var svc = new FakeSvc();
        var opts = new UpdateHostOptions
        {
            InstallDir = Install, StatePath = Path.Combine(_root, "s.json"), LegacyVersion = "1.0.0", ServerBaseUrl = new Uri("https://s"),
            Auth = () => null, ReadHealth = () => (null, null), HealthPoll = TimeSpan.FromMilliseconds(10),
        };
        var host = new WindowsUpdateHost(opts, new HttpClient(), svc, r => { reports.Add(r); return Task.CompletedTask; });
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        await host.SwitchToAsync("2.0.0");

        var resolved = await host.ResumePendingAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal("1.0.0", resolved);
        Assert.Equal("rolled_back", reports.Single().Phase);
        Assert.Equal(new UpdateState("1.0.0", null, null), host.State);
    }

    // ---- ACL re-apply invariant: sc config binPath= THEN sc sdset <HardenedServiceSddl>, on switch AND rollback ----

    private (WindowsUpdateHost host, List<string> sc) HostWithRealAclSequence()
    {
        var sc = new List<string>();
        var ctl = new WindowsUpdateServiceControl(
            run: (file, args) => { sc.Add($"{file} {args}"); return new ToolResult(0, ""); },
            serviceExists: _ => true, restart: () => sc.Add("restart"), setRunKey: _ => { });
        var opts = new UpdateHostOptions
        {
            InstallDir = Install, StatePath = Path.Combine(_root, "acl-state.json"), LegacyVersion = "1.0.0",
            ServerBaseUrl = new Uri("https://s"), Auth = () => null, ReadHealth = () => (null, null),
        };
        File.WriteAllText(Path.Combine(Install, "Windows.RunService.exe"), "x");
        var v2 = UpdateLayout.VersionDir(Install, "2.0.0");
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v2, "Windows.RunService.exe"), "x");
        return (new WindowsUpdateHost(opts, new HttpClient(), ctl, _ => Task.CompletedTask), sc);
    }

    [Fact]
    public async Task Switch_runs_sc_config_then_reapplies_the_hardened_sddl_before_restart()
    {
        var (host, sc) = HostWithRealAclSequence();
        await host.SwitchToAsync("2.0.0");
        var exe = Path.Combine(UpdateLayout.VersionDir(Install, "2.0.0"), "Windows.RunService.exe");
        Assert.Equal(
            [$"sc.exe config NISSMonitorService binPath= \"{exe}\"", $"sc.exe sdset NISSMonitorService {AgentSetup.HardenedServiceSddl}", "restart"], sc);
    }

    [Fact]
    public async Task Rollback_runs_sc_config_then_reapplies_the_hardened_sddl()
    {
        var (host, sc) = HostWithRealAclSequence();
        await host.SwitchToAsync("2.0.0");
        sc.Clear();
        await host.RollbackAsync("1.0.0");
        var exe = Path.Combine(Install, "Windows.RunService.exe");
        Assert.Equal(
            [$"sc.exe config NISSMonitorService binPath= \"{exe}\"", $"sc.exe sdset NISSMonitorService {AgentSetup.HardenedServiceSddl}", "restart"], sc);
        Assert.DoesNotContain(sc, c => c.Contains(AgentSetup.DefaultServiceSddl));   // never unhardened
    }

    // ---- guarded update loop ----

    [Fact]
    public async Task Exceptions_from_resume_and_ticks_do_not_stop_the_loop()
    {
        var errors = new List<string>();
        var ticks = 0;
        using var cts = new CancellationTokenSource();
        var loop = new UpdateLoop(_ => throw new InvalidOperationException("resume boom"),
            _ => { if (++ticks >= 3) cts.Cancel(); throw new DirectoryNotFoundException("tick boom"); },
            TimeSpan.FromMilliseconds(10), TimeProvider.System, (what, _) => errors.Add(what));

        await loop.RunAsync(cts.Token);   // returns normally, no exception escapes

        Assert.Equal(3, ticks);
        Assert.Equal("startup resume", errors[0]);
        Assert.Equal(4, errors.Count);
    }

    // ---- WatchdogChanged is surfaced, resume grace ----

    [Fact]
    public async Task WatchdogChanged_manifest_is_warned_about_not_silently_ignored()
    {
        var warnings = new List<string>();
        var opts = new UpdateHostOptions
        {
            InstallDir = Install, StatePath = Path.Combine(_root, "w.json"), LegacyVersion = "1.0.0", ServerBaseUrl = new Uri("https://s"),
            Auth = () => null, ReadHealth = () => (null, null), Warn = warnings.Add,
        };
        var host = new WindowsUpdateHost(opts, new HttpClient(), new FakeSvc(), _ => Task.CompletedTask);
        var d = new byte[] { 1 };
        var zip = Zip([("agent.exe", d)], new ReleaseManifest("2.0.0", null, [new ManifestFile("agent.exe", Sha(d))], true, null));
        await host.InstallSideBySideAsync("2.0.0", zip, CancellationToken.None);
        Assert.Contains("watchdog self-update is not supported", Assert.Single(warnings));
    }

    [Fact]
    public async Task Resume_tolerates_a_slow_service_start_within_the_longer_resume_grace()
    {
        var svc = new FakeSvc { Running = false };
        var opts = new UpdateHostOptions
        {
            InstallDir = Install, StatePath = Path.Combine(_root, "g.json"), LegacyVersion = "1.0.0", ServerBaseUrl = new Uri("https://s"),
            Auth = () => null, HealthPoll = TimeSpan.FromMilliseconds(10), StartupGrace = TimeSpan.FromMilliseconds(20),
            ReadHealth = () => (svc.Running ? "2.0.0" : null, svc.Running ? DateTimeOffset.UtcNow.AddSeconds(5) : null),
        };
        var host = new WindowsUpdateHost(opts, new HttpClient(), svc, _ => Task.CompletedTask);
        Directory.CreateDirectory(UpdateLayout.VersionDir(Install, "2.0.0"));
        await host.SwitchToAsync("2.0.0");
        _ = Task.Run(async () => { await Task.Delay(300); svc.Running = true; });   // delayed-start after reboot

        var resolved = await host.ResumePendingAsync(TimeSpan.FromSeconds(10), CancellationToken.None, TimeSpan.FromSeconds(5));

        Assert.Equal("2.0.0", resolved);
        Assert.Null(host.State.Pending);
    }
}
