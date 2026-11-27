using NISS.Monitor.Protocol;

namespace NISS.Api.Tests.Infrastructure;

/// <summary>Builders for realistic event payloads.</summary>
public static class P
{
    public static SessionStatePayload Session(string user = "alice", int sid = 1) => new(sid, user, "CORP", "Active", null);

    public static ApplicationForegroundPayload Foreground(Guid id, string process = "EXCEL.EXE", int pid = 100, string? window = "Budget.xlsx") =>
        new(id, pid, process, $@"C:\Program Files\{process}", window, null, 1);

    public static ApplicationBackgroundPayload Background(Guid id, DateTimeOffset start, DateTimeOffset end, double interaction, double idle = 0,
        string process = "EXCEL.EXE", int pid = 100, string reason = AppSessionEndReasons.Switched) =>
        new(id, pid, process, $@"C:\Program Files\{process}", "Budget.xlsx", start, end, (end - start).TotalSeconds, interaction, idle, reason, 1);

    public static ApplicationLifecyclePayload AppStarted(string process, int pid = 100, DateTimeOffset? opened = null) =>
        new(pid, process, $@"C:\Program Files\{process}", null, 1, opened, null);

    public static ProcessPayload Process(Guid id, string name, DateTimeOffset started, DateTimeOffset? ended = null, int pid = 100) =>
        new(id, pid, 4, name, $@"C:\Windows\{name}", "alice", 1, started, ended, null, null, false);

    public static IdlePayload Idle(DateTimeOffset? since, double? seconds = null, string user = "alice") => new(1, user, since, seconds, 300);

    public static InputActivityPayload Keyboard(DateTimeOffset bucket, int keys, DateTimeOffset? last = null) =>
        new(bucket, 30, keys, last ?? bucket.AddSeconds(20), null, null, null, null, null, null, null);

    public static InputActivityPayload Mouse(DateTimeOffset bucket, int moves, int clicks, int wheel = 0, int? x = null, int? y = null) =>
        new(bucket, 30, null, null, moves, clicks, 0, wheel, bucket.AddSeconds(10), x, y);

    public static UsbPayload Usb(string product = "Flash Drive") => new("MassStorage", "0781", "5581", "SanDisk", product, "SN123", @"USB\VID_0781", "E:", null, null);

    public static FilePayload File(string path = @"C:\Users\alice\Docs\report.docx", string action = "CREATED") =>
        new(path, System.IO.Path.GetFileName(path), ".docx", action, null, "alice");

    public static SystemMetricPayload Metric(double cpu = 25, double mem = 60, bool network = true) =>
        new(cpu, mem, 16384, 6553, [new DiskInfo("C:", 512, 200, 61)], 7200, network, ["10.0.0.5"], ["AA-BB-CC-DD-EE-FF"], null, [new DisplayInfo(1920, 1080)], "alice");

    public static AgentPayload Agent(string? message = null, bool? unexpected = null, string version = "1.0.0", string? component = null, string? interactiveUser = null) =>
        new(version, null, null, message, component, unexpected, null, interactiveUser);

    public static InventoryPayload Inventory() =>
        new("Dell Inc.", "Latitude 7440", "SVC12345", "Windows 11 Pro", "10.0.26100", "26100",
            "Intel Core i7-1365U", 10, 12, 16, 512,
            [new InstalledSoftware("Google Chrome", "130.0", "Google LLC"), new InstalledSoftware("7-Zip", "24.0", "Igor Pavlov")]);
}
