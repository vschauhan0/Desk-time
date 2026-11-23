using System.Net.NetworkInformation;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Native;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Collectors;

/// <summary>CPU / RAM / disk / network / battery / display sampling using cheap Win32 calls (no perf counters, no WMI on the hot path).</summary>
public sealed class SystemMetricsCollector : ISystemMetricsCollector
{
    private long _prevIdle, _prevTotal;
    private readonly object _gate = new();
    private IReadOnlyList<DisplayInfo>? _displays;
    private DateTimeOffset _displaysAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public SystemMetricPayload Collect()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref mem);
        var totalMb = mem.ullTotalPhys / 1048576.0;
        var availMb = mem.ullAvailPhys / 1048576.0;

        var (ips, macs, connected) = ReadNetwork();
        return new SystemMetricPayload(
            Math.Round(ReadCpu(), 1),
            totalMb > 0 ? Math.Round((totalMb - availMb) / totalMb * 100, 1) : 0,
            Math.Round(totalMb, 0), Math.Round(availMb, 0),
            ReadDisks(),
            (long)(NativeMethods.GetTickCount64() / 1000),
            connected, ips, macs, ReadBattery(), ReadDisplays(),
            Environment.UserName);
    }

    private double ReadCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var i = idle.ToLong();
        var total = kernel.ToLong() + user.ToLong(); // kernel time includes idle time
        lock (_gate)
        {
            var dIdle = i - _prevIdle;
            var dTotal = total - _prevTotal;
            var first = _prevTotal == 0;
            _prevIdle = i; _prevTotal = total;
            if (first || dTotal <= 0) return 0;
            return Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);
        }
    }

    private static List<DiskInfo> ReadDisks()
    {
        var list = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var total = d.TotalSize / 1073741824.0;
                var free = d.AvailableFreeSpace / 1073741824.0;
                list.Add(new DiskInfo(d.Name.TrimEnd('\\'), Math.Round(total, 1), Math.Round(free, 1), total > 0 ? Math.Round((total - free) / total * 100, 1) : 0));
            }
            catch (IOException) { }
        }
        return list;
    }

    private static (List<string> Ips, List<string> Macs, bool Connected) ReadNetwork()
    {
        var ips = new List<string>();
        var macs = new List<string>();
        var connected = false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var v4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                           && !a.Address.ToString().StartsWith("169.254", StringComparison.Ordinal)).ToList();
                if (v4.Count == 0) continue;
                connected = true;
                ips.AddRange(v4.Select(a => a.Address.ToString()));
                var mac = nic.GetPhysicalAddress().ToString();
                if (mac.Length == 12) macs.Add(string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))));
            }
        }
        catch (NetworkInformationException) { }
        return (ips.Distinct().ToList(), macs.Distinct().ToList(), connected);
    }

    private static BatteryInfo? ReadBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var p)) return null;
        var present = (p.BatteryFlag & 128) == 0 && p.BatteryFlag != 255; // 128 = no system battery
        if (!present) return new BatteryInfo(false, null, null, null);
        int? pct = p.BatteryLifePercent == 255 ? null : p.BatteryLifePercent;
        int? minutes = p.BatteryLifeTime == 0xFFFFFFFF ? null : (int)(p.BatteryLifeTime / 60);
        return new BatteryInfo(true, pct, p.ACLineStatus == 1, minutes);
    }

    private IReadOnlyList<DisplayInfo> ReadDisplays()
    {
        if (_displays is not null && DateTimeOffset.UtcNow - _displaysAt < TimeSpan.FromHours(1)) return _displays;
        var list = new List<DisplayInfo>();
        try
        {
            var count = NativeMethods.GetSystemMetrics(NativeMethods.SM_CMONITORS);
            var w = NativeMethods.GetSystemMetrics(0);
            var h = NativeMethods.GetSystemMetrics(1);
            if (w > 0 && h > 0) for (var i = 0; i < Math.Max(1, count); i++) list.Add(new DisplayInfo(w, h));
            else
            {
                // The service runs in session 0 without a desktop: ask the video controllers instead.
                using var s = new System.Management.ManagementObjectSearcher("SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");
                foreach (var o in s.Get())
                    if (o["CurrentHorizontalResolution"] is uint hr && o["CurrentVerticalResolution"] is uint vr && hr > 0) list.Add(new DisplayInfo((int)hr, (int)vr));
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException) { }
        _displays = list;
        _displaysAt = DateTimeOffset.UtcNow;
        return list;
    }
}
