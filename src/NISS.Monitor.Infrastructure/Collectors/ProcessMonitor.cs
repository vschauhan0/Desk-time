using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Configuration;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Native;
using NISS.Monitor.Infrastructure.Persistence;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Collectors;

/// <summary>
/// Process start/stop monitoring. A cheap toolhelp snapshot every couple of seconds is diffed by
/// <see cref="ProcessDiffEngine"/>; details (path, user, real start time) are only fetched for *new* processes.
/// One process = one deterministic <c>ProcessSessionId</c>, so restarts of the agent never duplicate a process.
/// </summary>
public sealed class ProcessMonitor : IProcessMonitor
{
    private readonly IEventSink _sink;
    private readonly IPolicyProvider _policy;
    private readonly LocalTelemetryStore _local;
    private readonly DeviceIdentityStore _identity;
    private readonly ICollectorRegistry _registry;
    private readonly TimeProvider _time;
    private readonly AgentSettings _settings;
    private readonly ILogger<ProcessMonitor> _log;
    private readonly ProcessDiffEngine _diff = new();
    private readonly Dictionary<int, ProcessPayload> _active = [];
    private readonly Dictionary<string, (DateTime Modified, string Hash)> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<(Guid Id, ProcessPayload Payload)> _stored = [];

    public ProcessMonitor(IEventSink sink, IPolicyProvider policy, LocalTelemetryStore local, DeviceIdentityStore identity,
        ICollectorRegistry registry, TimeProvider time, IOptions<AgentSettings> settings, ILogger<ProcessMonitor> log)
    {
        _sink = sink; _policy = policy; _local = local; _identity = identity; _registry = registry; _time = time;
        _settings = settings.Value; _log = log;
    }

    /// <inheritdoc />
    public string Name => "ProcessMonitor";

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _stored = _local.LoadOpenProcessSessions();
        var poll = TimeSpan.FromSeconds(Math.Max(1, _settings.Monitoring.ProcessPollSeconds));
        using var timer = new PeriodicTimer(poll);
        _registry.Report(Name, "Running");
        do
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One snapshot/diff cycle (public for tests).</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var policy = _policy.Current;
        var filter = new ProcessFilter(policy.ProcessMonitoringMode, policy.ProcessExclusions);
        var snapshot = TakeSnapshot().Where(filter.ShouldTrack).ToList();
        var now = _time.GetUtcNow();
        var diff = _diff.Update(snapshot);
        var deviceId = _identity.Current?.DeviceId ?? "unenrolled";

        foreach (var p in diff.Stopped)
        {
            if (!_active.Remove(p.Pid, out var payload)) continue;
            var stopped = payload with { EndedUtc = now, Baseline = false };
            _local.ProcessStopped(payload.ProcessSessionId, now);
            await EmitAsync(EventTypes.ProcessStopped, stopped, now, ct).ConfigureAwait(false);
        }

        foreach (var p in diff.Started)
        {
            var payload = Describe(p, policy, deviceId, now, diff.IsBaseline);
            if (diff.IsBaseline && MatchesStored(payload, out _))
            {
                _active[p.Pid] = payload; // already reported before the agent restarted
                continue;
            }
            _active[p.Pid] = payload;
            _local.ProcessStarted(payload);
            await EmitAsync(EventTypes.ProcessStarted, payload, payload.StartedUtc, ct).ConfigureAwait(false);
        }

        if (diff.IsBaseline)
        {
            // Processes that were open when the agent last stopped but are gone now ended while we were not watching.
            foreach (var (id, stored) in _stored.Where(s => !_active.Values.Any(a => a.ProcessSessionId == s.Id)))
            {
                var ended = stored with { EndedUtc = now };
                _local.ProcessStopped(id, now);
                await EmitAsync(EventTypes.ProcessStopped, ended, now, ct).ConfigureAwait(false);
            }
            _stored = [];
            _log.LogInformation("Process baseline established: {Count} tracked processes", _active.Count);
        }
        _registry.Touch(Name);
    }

    private bool MatchesStored(ProcessPayload p, out Guid id)
    {
        foreach (var (sid, s) in _stored)
            if (s.ProcessId == p.ProcessId && Math.Abs((s.StartedUtc - p.StartedUtc).TotalSeconds) < 2 &&
                string.Equals(s.ProcessName, p.ProcessName, StringComparison.OrdinalIgnoreCase)) { id = sid; return true; }
        id = Guid.Empty;
        return false;
    }

    private ProcessPayload Describe(ProcessSnapshot p, AgentPolicy policy, string deviceId, DateTimeOffset now, bool baseline)
    {
        var path = NativeMethods.GetProcessImagePath(p.Pid);
        var start = NativeMethods.GetProcessStartTime(p.Pid) ?? now;
        var user = NativeMethods.GetProcessUser(p.Pid);
        var sessionId = DeterministicGuid.Create($"{deviceId}|{p.Pid}|{start.UtcTicks}");
        return new ProcessPayload(sessionId, p.Pid, p.ParentPid <= 0 ? null : p.ParentPid, p.Name, path, user, p.SessionId, start, null,
            policy.CaptureCommandLines ? CommandLineReader.TryGet(p.Pid) : null,
            policy.ComputeProcessHashes ? Hash(path) : null, baseline);
    }

    private string? Hash(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            if (_hashCache.TryGetValue(path, out var c) && c.Modified == modified) return c.Hash;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            _hashCache[path] = (modified, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private ValueTask EmitAsync(string type, ProcessPayload payload, DateTimeOffset at, CancellationToken ct)
    {
        // Deterministic id: a given process lifetime has exactly one start and one stop event.
        var id = DeterministicGuid.Create($"{payload.ProcessSessionId}|{type}");
        return _sink.EmitAsync(EventDraft.Create(type, EventSources.Service, at, payload, null, id), ct);
    }

    /// <summary>Cheap process list via CreateToolhelp32Snapshot.</summary>
    public static List<ProcessSnapshot> TakeSnapshot()
    {
        var list = new List<ProcessSnapshot>(256);
        var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snap, ref entry)) return list;
            do
            {
                var pid = (int)entry.th32ProcessID;
                list.Add(new ProcessSnapshot(pid, (int)entry.th32ParentProcessID, entry.szExeFile, NativeMethods.GetProcessSessionId(pid)));
            }
            while (NativeMethods.Process32NextW(snap, ref entry));
        }
        finally { NativeMethods.CloseHandle(snap); }
        return list;
    }
}

/// <summary>Reads a process command line through WMI. Only used when the policy explicitly enables it.</summary>
internal static class CommandLineReader
{
    public static string? TryGet(int pid)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
            foreach (var o in s.Get()) return o["CommandLine"] as string;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or COMException or UnauthorizedAccessException) { }
        return null;
    }
}
