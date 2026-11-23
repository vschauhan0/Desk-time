using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NISS.Monitor.Infrastructure.Native;

namespace NISS.Monitor.Infrastructure.Identity;

/// <summary>Static-ish device facts. Only what is needed for enrollment and inventory - nothing personal.</summary>
public sealed class MachineInfo
{
    private readonly Lazy<Facts> _facts = new(Load);

    public string Hostname => Environment.MachineName;
    public string? MachineGuid => _facts.Value.MachineGuid;
    public string? Serial => _facts.Value.Serial;
    public string? Manufacturer => _facts.Value.Manufacturer;
    public string? Model => _facts.Value.Model;
    public string OsVersion => RuntimeInformation.OSDescription;

    /// <summary>Name of the interactive user, if one is logged in (the service itself runs as SYSTEM).</summary>
    public string InteractiveUserOrCurrent()
    {
        var active = NativeMethods.WTSGetActiveConsoleSessionId();
        if (active != 0xFFFFFFFF && WtsQuery.QueryString((int)active, NativeMethods.WTSUserName) is { Length: > 0 } user)
            return user;
        return Environment.UserName;
    }

    private sealed record Facts(string? MachineGuid, string? Serial, string? Manufacturer, string? Model);

    private static Facts Load()
    {
        string? guid = null, serial = null, maker = null, model = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = key?.GetValue("MachineGuid") as string;
        }
        catch { /* best effort */ }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (var o in s.Get()) { serial = (o["SerialNumber"] as string)?.Trim(); break; }
            using var m = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var o in m.Get()) { maker = (o["Manufacturer"] as string)?.Trim(); model = (o["Model"] as string)?.Trim(); break; }
        }
        catch { /* WMI unavailable: inventory fields stay null */ }
        if (serial is "To Be Filled By O.E.M." or "Default string" or "0") serial = null;
        return new Facts(guid, serial, maker, model);
    }
}

/// <summary>Helper around WTSQuerySessionInformation for string values.</summary>
internal static class WtsQuery
{
    public static string? QueryString(int sessionId, int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buf, out var bytes) || buf == IntPtr.Zero) return null;
        try { return bytes > 2 ? Marshal.PtrToStringUni(buf) : null; }
        finally { NativeMethods.WTSFreeMemory(buf); }
    }
}
