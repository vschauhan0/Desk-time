using System.Security.Cryptography;
using System.Text;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

public sealed class PackageVerificationTests
{
    private const string Cur = "1.0.0";

    private sealed class FakeSig(Func<byte[], PublisherPin, bool> f) : ISignatureChecker
    {
        public bool IsAuthenticodeSignedBy(byte[] fileBytes, PublisherPin pin) => f(fileBytes, pin);
    }

    private static readonly byte[] Exe = Encoding.UTF8.GetBytes("exe-bytes");
    private static readonly byte[] Cfg = Encoding.UTF8.GetBytes("{\"a\":1}");

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static ReleaseManifest Manifest(string version = "1.1.0", string? exeSha = null) => new(
        version, null,
        [new ManifestFile("agent.exe", exeSha ?? Sha(Exe)), new ManifestFile("settings.json", Sha(Cfg))],
        false, null);

    private static byte[] Read(string path) => path == "agent.exe" ? Exe : Cfg;

    private static readonly PublisherPin Pin = new(["CN=Acme"]);

    [Fact]
    public void Verify_passes_for_a_good_manifest()
    {
        var r = PackageVerification.Verify(Manifest(), Read, new FakeSig((_, _) => true), Pin, Cur);
        Assert.True(r.Ok);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Tampered_file_sha_mismatch_fails()
    {
        var r = PackageVerification.Verify(Manifest(exeSha: Sha(Cfg)), Read, new FakeSig((_, _) => true), Pin, Cur);
        Assert.False(r.Ok);
        Assert.Contains("agent.exe", r.Error);
    }

    [Fact]
    public void Unsigned_or_wrong_publisher_exe_fails()
    {
        var r = PackageVerification.Verify(Manifest(), Read, new FakeSig((_, _) => false), Pin, Cur);
        Assert.False(r.Ok);
        Assert.Contains("agent.exe", r.Error);
    }

    [Fact]
    public void Non_executables_are_not_signature_checked()
    {
        var checkedBytes = new List<byte[]>();
        var r = PackageVerification.Verify(Manifest(), Read, new FakeSig((b, _) => { checkedBytes.Add(b); return true; }), Pin, Cur);
        Assert.True(r.Ok);
        Assert.Single(checkedBytes);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.9")]
    public void Older_or_equal_version_fails(string version)
    {
        var r = PackageVerification.Verify(Manifest(version), Read, new FakeSig((_, _) => true), Pin, Cur);
        Assert.False(r.Ok);
        Assert.Contains("version", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dual_trust_file_signed_by_second_accepted_subject_passes()
    {
        var dual = new PublisherPin(["CN=Old", "CN=New"]);
        // Fake: file is signed only by the second subject; passes iff the pin accepts it.
        var sig = new FakeSig((_, p) => p.AcceptedSubjects.Contains("CN=New"));
        Assert.True(PackageVerification.Verify(Manifest(), Read, sig, dual, Cur).Ok);
        Assert.False(PackageVerification.Verify(Manifest(), Read, sig, new PublisherPin(["CN=Old"]), Cur).Ok);
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("garbage", "1.0.0", false)]
    public void VersionCompare_IsNewer(string cand, string cur, bool expected) =>
        Assert.Equal(expected, VersionCompare.IsNewer(cand, cur));
}
