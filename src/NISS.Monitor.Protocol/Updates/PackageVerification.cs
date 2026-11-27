using System.Security.Cryptography;

namespace NISS.Monitor.Protocol;

public readonly record struct VerifyResult(bool Ok, string? Error)
{
    public static VerifyResult Success => new(true, null);
    public static VerifyResult Fail(string error) => new(false, error);
}

public static class VersionCompare
{
    /// <summary>True only when both parse as versions and <paramref name="candidate"/> is strictly greater.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var c) && Version.TryParse(current, out var cur) && c > cur;
}

public static class PackageVerification
{
    private static readonly string[] ExecutableExtensions = [".exe", ".dll"];

    /// <summary>Signed-or-it-doesn't-run: newer version, every sha256 matches, every executable signed by the pin.</summary>
    public static VerifyResult Verify(ReleaseManifest m, Func<string, byte[]> readFile,
        ISignatureChecker sig, PublisherPin pin, string currentVersion)
    {
        if (!VersionCompare.IsNewer(m.Version, currentVersion))
            return VerifyResult.Fail($"Version {m.Version} is not newer than current {currentVersion}.");

        foreach (var f in m.Files)
        {
            byte[] bytes;
            try { bytes = readFile(f.Path); }
            catch (Exception ex) { return VerifyResult.Fail($"Cannot read {f.Path}: {ex.Message}"); }

            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return VerifyResult.Fail($"SHA-256 mismatch for {f.Path}.");

            if (IsExecutable(f.Path) && !sig.IsAuthenticodeSignedBy(bytes, pin))
                return VerifyResult.Fail($"{f.Path} is not signed by an accepted publisher.");
        }
        return VerifyResult.Success;
    }

    private static bool IsExecutable(string path) =>
        ExecutableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
