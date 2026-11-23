using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Collectors;

/// <summary>NETWORK_CONNECTED / NETWORK_DISCONNECTED from OS change notifications (debounced).</summary>
public sealed class NetworkMonitor : ICollector
{
    private readonly IEventSink _sink;
    private readonly ISystemMetricsCollector _metrics;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;

    public NetworkMonitor(IEventSink sink, ISystemMetricsCollector metrics, ICollectorRegistry registry, TimeProvider time)
    {
        _sink = sink; _metrics = metrics; _registry = registry; _time = time;
    }

    public string Name => "NetworkMonitor";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnChange(object? s, EventArgs e) => signal.Writer.TryWrite(true);
        NetworkChange.NetworkAvailabilityChanged += OnChange;
        NetworkChange.NetworkAddressChanged += OnChange;
        try
        {
            _registry.Report(Name, "Running");
            bool? last = null;
            while (true)
            {
                var m = _metrics.Collect();
                if (last != m.NetworkConnected)
                {
                    var payload = new NetworkPayload(m.NetworkConnected, m.IpAddresses, null);
                    await _sink.EmitAsync(EventDraft.Create(m.NetworkConnected ? EventTypes.NetworkConnected : EventTypes.NetworkDisconnected,
                        EventSources.Service, _time.GetUtcNow(), payload), cancellationToken).ConfigureAwait(false);
                    last = m.NetworkConnected;
                    _registry.Touch(Name);
                }
                await signal.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); // debounce flapping
            }
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnChange;
            NetworkChange.NetworkAddressChanged -= OnChange;
        }
    }
}

/// <summary>
/// USB arrival/removal via a periodic WMI PnP snapshot diff. Only device *identity* is recorded (class, vendor,
/// product, serial) - the monitor never touches removable-media contents.
/// </summary>
public sealed partial class UsbMonitor : IUsbMonitor
{
    private readonly IEventSink _sink;
    private readonly IPolicyProvider _policy;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<UsbMonitor> _log;
    private Dictionary<string, UsbPayload>? _known;

    public UsbMonitor(IEventSink sink, IPolicyProvider policy, ICollectorRegistry registry, TimeProvider time, ILogger<UsbMonitor> log)
    {
        _sink = sink; _policy = policy; _registry = registry; _time = time; _log = log;
    }

    public string Name => "UsbMonitor";

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _registry.Report(Name, "Running");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            if (_policy.Current.UsbMonitoringEnabled) await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Snapshot and diff once (public for tests).</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var current = Snapshot();
        var now = _time.GetUtcNow();
        if (_known is null)
        {
            _known = current; // baseline: devices already attached are not "connected" events
            return;
        }
        foreach (var (key, dev) in current.Where(kv => !_known.ContainsKey(kv.Key)))
        {
            await _sink.EmitAsync(EventDraft.Create(EventTypes.UsbConnected, EventSources.Service, now, dev with { ArrivedUtc = now }), ct).ConfigureAwait(false);
            _known[key] = dev with { ArrivedUtc = now };
            _registry.Touch(Name);
        }
        foreach (var key in _known.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            var dev = _known[key];
            await _sink.EmitAsync(EventDraft.Create(EventTypes.UsbDisconnected, EventSources.Service, now, dev with { RemovedUtc = now }), ct).ConfigureAwait(false);
            _known.Remove(key);
            _registry.Touch(Name);
        }
    }

    private Dictionary<string, UsbPayload> Snapshot()
    {
        var map = new Dictionary<string, UsbPayload>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                @"SELECT PNPDeviceID, Caption, Manufacturer, Service, PNPClass FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID%'");
            foreach (System.Management.ManagementBaseObject o in s.Get())
            {
                var id = o["PNPDeviceID"] as string;
                if (string.IsNullOrEmpty(id) || id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue; // interface children
                var service = (o["Service"] as string)?.ToLowerInvariant();
                if (service is "usbhub" or "usbhub3" or "usbxhci" or "usbehci") continue;
                var m = VidPid().Match(id);
                var serialPart = id[(id.LastIndexOf('\\') + 1)..];
                var serial = serialPart.Contains('&') ? null : serialPart; // generated ids ("5&2a3b&0&1") are not real serials
                var vendor = o["Manufacturer"] as string;
                if (vendor is not null && vendor.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase)) vendor = null;
                map[id] = new UsbPayload(Classify(service, o["PNPClass"] as string), m.Success ? m.Groups[1].Value : null, m.Success ? m.Groups[2].Value : null,
                    vendor, o["Caption"] as string, serial, id, null, null, null);
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            _log.LogWarning(ex, "USB enumeration failed");
            _registry.Report(Name, "Degraded", ex.Message);
        }
        return map;
    }

    private static string Classify(string? service, string? pnpClass) => service switch
    {
        "usbstor" or "uaspstor" => "Storage",
        "hidusb" => "HID",
        "usbvideo" => "Camera",
        "usbprint" => "Printer",
        "usbaudio" or "usbaudio2" => "Audio",
        "bthusb" => "Bluetooth",
        "wudfwpdmtp" => "Phone/MTP",
        _ => pnpClass ?? "USB",
    };
}

/// <summary>
/// Optional file metadata watcher (default OFF). Watches only administrator-configured paths and reports
/// path/name/extension/action - never contents.
/// </summary>
public sealed class FileActivityMonitor : IFileActivityMonitor
{
    private readonly IEventSink _sink;
    private readonly IPolicyProvider _policy;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;
    private readonly MachineInfo _machine;
    private readonly ILogger<FileActivityMonitor> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Channel<(string Type, FilePayload Payload)> _events = Channel.CreateBounded<(string, FilePayload)>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });
    private IReadOnlyList<string> _ignored = [];

    public FileActivityMonitor(IEventSink sink, IPolicyProvider policy, ICollectorRegistry registry, TimeProvider time, MachineInfo machine, ILogger<FileActivityMonitor> log)
    {
        _sink = sink; _policy = policy; _registry = registry; _time = time; _machine = machine; _log = log;
    }

    public string Name => "FileActivityMonitor";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _registry.Report(Name, "Disabled");
        void OnPolicy(AgentPolicy p) => Reconfigure(p);
        _policy.Changed += OnPolicy;
        try
        {
            Reconfigure(_policy.Current);
            await foreach (var (type, payload) in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _sink.EmitAsync(EventDraft.Create(type, EventSources.Service, _time.GetUtcNow(), payload), cancellationToken).ConfigureAwait(false);
                _registry.Touch(Name);
            }
        }
        finally
        {
            _policy.Changed -= OnPolicy;
            DisposeWatchers();
        }
    }

    private void Reconfigure(AgentPolicy policy)
    {
        lock (_watchers)
        {
            DisposeWatchers();
            var fm = policy.FileMonitoring;
            _ignored = fm.IgnoreExtensions.Select(e => e.StartsWith('.') ? e : "." + e).ToList();
            if (!fm.Enabled || fm.Paths.Count == 0) { _registry.Report(Name, "Disabled"); return; }

            foreach (var path in fm.Paths)
            {
                try
                {
                    if (!Directory.Exists(path)) { _log.LogWarning("File monitoring path {Path} does not exist", path); continue; }
                    var w = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = false };
                    w.Created += (_, e) => Queue(EventTypes.FileCreated, e.FullPath, null);
                    w.Changed += (_, e) => Queue(EventTypes.FileModified, e.FullPath, null);
                    w.Deleted += (_, e) => Queue(EventTypes.FileDeleted, e.FullPath, null);
                    w.Renamed += (_, e) => Queue(EventTypes.FileRenamed, e.FullPath, e.OldFullPath);
                    w.Error += (_, e) => _log.LogWarning(e.GetException(), "File watcher error on {Path}", path);
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "Cannot watch {Path}", path);
                }
            }
            _registry.Report(Name, _watchers.Count > 0 ? "Running" : "Degraded", _watchers.Count > 0 ? null : "No watchable paths");
        }
    }

    private void Queue(string type, string fullPath, string? oldPath)
    {
        try
        {
            if (Directory.Exists(fullPath)) return; // directories are not reported
            var ext = Path.GetExtension(fullPath);
            if (_ignored.Contains(ext, StringComparer.OrdinalIgnoreCase)) return;

            // A single save produces a burst of Changed events: coalesce per path+action.
            var key = type + "|" + fullPath;
            var now = _time.GetUtcNow();
            if (_recent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(2)) return;
            _recent[key] = now;
            if (_recent.Count > 5000) foreach (var k in _recent.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList()) _recent.TryRemove(k, out _);

            _events.Writer.TryWrite((type, new FilePayload(fullPath, Path.GetFileName(fullPath), string.IsNullOrEmpty(ext) ? null : ext,
                type[5..].ToLowerInvariant(), oldPath, _machine.InteractiveUserOrCurrent())));
        }
        catch (ArgumentException) { /* invalid path characters: ignore */ }
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
