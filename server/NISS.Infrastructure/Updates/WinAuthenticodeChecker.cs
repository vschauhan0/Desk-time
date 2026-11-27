using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NISS.Application.Updates;
using NISS.Monitor.Protocol;

namespace NISS.Infrastructure.Updates;

/// <summary>
/// Windows Authenticode verification of file bytes against a publisher-identity pin.
/// Two steps: WinVerifyTrust (the signature is valid, the file is unmodified, the chain is trusted by this machine),
/// then the LEAF signer certificate subject only (never an issuer or chain subject) must equal ANY accepted pin
/// subject. Never a leaf-thumbprint comparison, so a certificate renewal under the same subject keeps working.
/// Not unit-tested (needs real signed binaries); verify manually with a signed package.
/// </summary>
internal sealed class WinAuthenticodeChecker : ISignatureChecker, ISignerSubjectReader
{
    public bool IsAuthenticodeSignedBy(byte[] fileBytes, PublisherPin pin)
    {
        if (!OperatingSystem.IsWindows() || pin.AcceptedSubjects.Count == 0) return false;
        return WithTempFile(fileBytes, path =>
        {
            if (!WinVerifyTrustFile(path)) return false;
            using var signer = LoadSigner(path);
            if (signer is null) return false;
            // Leaf publisher only: pinning a CA/intermediate subject must NOT trust everything that CA issued.
            return MatchesAny(signer.Subject, pin);
        });
    }

    public string? GetSignerSubject(byte[] fileBytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return WithTempFile(fileBytes, path => { using var c = LoadSigner(path); return c?.Subject; });
    }

    private static bool MatchesAny(string subject, PublisherPin pin) =>
        pin.AcceptedSubjects.Any(s => string.Equals(s.Trim(), subject.Trim(), StringComparison.OrdinalIgnoreCase));

    private static X509Certificate2? LoadSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile has no replacement for Authenticode
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>".dll" when the PE header has IMAGE_FILE_DLL set, else ".exe", so WinVerifyTrust sees the real file type.</summary>
    internal static string PeExtension(byte[] b)
    {
        if (b.Length < 0x40) return ".exe";
        var pe = BitConverter.ToInt32(b, 0x3C);
        var chOffset = pe + 4 + 18;
        if (pe < 0 || chOffset + 2 > b.Length) return ".exe";
        return (BitConverter.ToUInt16(b, chOffset) & 0x2000) != 0 ? ".dll" : ".exe";
    }

    private static T WithTempFile<T>(byte[] bytes, Func<string, T> use)
    {
        var path = Path.Combine(Path.GetTempPath(), $"niss-verify-{Guid.NewGuid():N}{PeExtension(bytes)}");
        try
        {
            File.WriteAllBytes(path, bytes);
            return use(path);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ---- WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2) -------------------------------------------------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool WinVerifyTrustFile(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,                 // WTD_UI_NONE
                fdwRevocationChecks = 0,        // WTD_REVOKE_NONE (the server may be offline from CRL hosts)
                dwUnionChoice = 1,              // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,              // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00001000,       // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
