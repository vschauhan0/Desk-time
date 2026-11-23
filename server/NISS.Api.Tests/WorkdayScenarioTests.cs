using System.Net;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>
/// One realistic working day posted through the API (login, Excel, Chrome, idle, lock/unlock, USB, metrics, logout)
/// and the numbers the read APIs and reports derive from it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WorkdayScenarioTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Day = new(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int h, int m, int s = 0) => Day.AddHours(h).AddMinutes(m).AddSeconds(s);

    private sealed record Seeded(TestDevice Device, Guid EmployeeId);

    private HttpClient _admin = null!;
    private TestDevice _device = null!;
    private Guid _employeeId;

    public async Task InitializeAsync()
    {
        _admin = await factory.AdminAsync();
        var seeded = await factory.OnceAsync("workday", SeedAsync);
        (_device, _employeeId) = (seeded.Device, seeded.EmployeeId);
    }

    private async Task<Seeded> SeedAsync()
    {
        var admin = await factory.AdminAsync();
        await admin.PostJsonAsync("/api/v1/employees", new EmployeeUpsert("EMP-DAY", "Dana Day", "Finance", "dana@example.test", true));
        var d = await factory.EnrollDeviceAsync(admin, "EMP-DAY", user: "dana");

        var excelA = Guid.NewGuid(); var excelC = Guid.NewGuid(); var chromeB = Guid.NewGuid();
        var chromeProc = Guid.NewGuid(); var excelProc = Guid.NewGuid();

        // Delivered in three batches, like an agent draining its queue.
        await d.IngestAsync(
            d.Event(EventTypes.UserLogin, At(9, 0), P.Session("dana")),
            d.Event(EventTypes.ProcessStarted, At(8, 59), P.Process(chromeProc, "chrome.exe", At(8, 59), pid: 200)),
            d.Event(EventTypes.ApplicationStarted, At(8, 59, 5), P.AppStarted("chrome.exe", 200)),
            d.Event(EventTypes.ProcessStarted, At(9, 0), P.Process(excelProc, "EXCEL.EXE", At(9, 0), pid: 100)),
            d.Event(EventTypes.ApplicationStarted, At(9, 0), P.AppStarted("EXCEL.EXE", 100)),
            d.Event(EventTypes.SystemMetric, At(9, 0), P.Metric(20, 50)),
            d.Event(EventTypes.ApplicationForeground, At(9, 0, 5), P.Foreground(excelA, "EXCEL.EXE", 100, "Q1.xlsx")),
            d.Event(EventTypes.KeyboardActivity, At(9, 1), P.Keyboard(At(9, 0, 30), 80)),
            d.Event(EventTypes.MouseActivity, At(9, 1), P.Mouse(At(9, 0, 30), 200, 12)),
            d.Event(EventTypes.ApplicationBackground, At(9, 30, 5), P.Background(excelA, At(9, 0, 5), At(9, 30, 5), interaction: 1500, idle: 0)),
            d.Event(EventTypes.ApplicationForeground, At(9, 30, 5), P.Foreground(chromeB, "chrome.exe", 200, "Docs")),
            d.Event(EventTypes.SystemMetric, At(9, 30), P.Metric(35, 55)));

        await d.IngestAsync(
            d.Event(EventTypes.IdleStarted, At(10, 0), P.Idle(At(9, 55))),
            d.Event(EventTypes.SystemMetric, At(10, 0), P.Metric(5, 52)),
            d.Event(EventTypes.IdleEnded, At(10, 10), P.Idle(At(9, 55), 900)),
            d.Event(EventTypes.ApplicationBackground, At(10, 20, 5), P.Background(chromeB, At(9, 30, 5), At(10, 20, 5), interaction: 1800, idle: 900, process: "chrome.exe", pid: 200)),
            d.Event(EventTypes.SessionLock, At(10, 20, 5), P.Session("dana")),
            d.Event(EventTypes.SessionUnlock, At(10, 35, 5), P.Session("dana")),
            d.Event(EventTypes.ApplicationForeground, At(10, 36), P.Foreground(excelC, "EXCEL.EXE", 100, "Q2.xlsx")),
            d.Event(EventTypes.UsbConnected, At(10, 40), P.Usb("Backup Stick")));

        await d.IngestAsync(
            d.Event(EventTypes.ApplicationBackground, At(10, 50), P.Background(excelC, At(10, 36), At(10, 50), interaction: 600, idle: 0)),
            d.Event(EventTypes.ProcessStopped, At(10, 55), P.Process(excelProc, "EXCEL.EXE", At(9, 0), At(10, 55), 100)),
            d.Event(EventTypes.UserLogout, At(11, 0), P.Session("dana")),
            d.Event(EventTypes.ProcessStopped, At(11, 0), P.Process(chromeProc, "chrome.exe", At(8, 59), At(11, 0), 200)));

        var employees = await admin.GetJsonAsync<Paged<EmployeeDto>>("/api/v1/employees?search=EMP-DAY");
        return new Seeded(d, employees.Items.Single().Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string Range => $"from={Uri.EscapeDataString(At(0, 0).ToString("o"))}&to={Uri.EscapeDataString(At(23, 59).ToString("o"))}";

    [Fact]
    public async Task Employee_day_summary_has_the_expected_numbers()
    {
        var s = await _admin.GetJsonAsync<EmployeeDaySummary>($"/api/v1/employees/{_employeeId}/summary?date=2026-03-10&tzOffsetMinutes=0");

        Assert.Equal("2026-03-10", s.Date);
        Assert.Equal("EMP-DAY", s.Employee.EmployeeCode);
        Assert.Equal(At(9, 0), s.FirstLoginUtc);
        Assert.Equal(At(11, 0), s.LastLogoutUtc);
        Assert.False(s.LoggedInNow);
        Assert.Equal(7200, s.LoggedInSeconds);          // 09:00-11:00
        Assert.Equal(900, s.IdleSeconds);               // 09:55-10:10
        Assert.Equal(900, s.LockedSeconds);             // 10:20:05-10:35:05
        Assert.Equal(5400, s.ActiveSeconds);            // logged in minus idle minus locked
        Assert.Equal(s.LoggedInSeconds, s.ActiveSeconds + s.IdleSeconds + s.LockedSeconds);
        Assert.Equal([_device.DeviceId], s.Devices);

        var excel = s.Applications.Single(a => a.ProcessName == "EXCEL");
        Assert.Equal("Office", excel.Category);
        Assert.Equal(6900, excel.RunningSeconds);        // process lifetime 09:00-10:55: running is NOT the same as used
        Assert.Equal(2640, excel.ForegroundSeconds);     // 1800 + 840
        Assert.Equal(2100, excel.InteractionSeconds);    // 1500 + 600
        Assert.Equal(1, excel.Launches);
        Assert.Equal(2, excel.ForegroundTransitions);

        var chrome = s.Applications.Single(a => a.ProcessName == "chrome");
        Assert.Equal("Browser", chrome.Category);
        Assert.Equal(7260, chrome.RunningSeconds);       // running all day (08:59-11:00)
        Assert.Equal(3000, chrome.ForegroundSeconds);
        Assert.Equal(1800, chrome.InteractionSeconds);
        Assert.True(chrome.RunningSeconds > chrome.ForegroundSeconds && chrome.ForegroundSeconds > chrome.InteractionSeconds);
        Assert.Equal(1, chrome.Launches);
        Assert.Equal(1, chrome.ForegroundTransitions);
        Assert.Equal(["chrome", "EXCEL"], s.Applications.Select(a => a.ProcessName));   // ordered by foreground time
    }

    [Fact]
    public async Task Summary_is_clipped_to_the_requested_local_day()
    {
        // Seen from UTC-10 the local day 2026-03-10 starts at 10:00Z: only 10:00-11:00 of the shift belongs to it.
        var late = await _admin.GetJsonAsync<EmployeeDaySummary>($"/api/v1/employees/{_employeeId}/summary?date=2026-03-10&tzOffsetMinutes=-600");
        Assert.Equal(3600, late.LoggedInSeconds);
        Assert.Equal(600, late.IdleSeconds);        // 09:55-10:10 clipped to 10:00-10:10
        Assert.Equal(900, late.LockedSeconds);
        Assert.Equal(2100, late.ActiveSeconds);
        Assert.Equal(1205, late.Applications.Single(a => a.ProcessName == "chrome").ForegroundSeconds);   // 10:00-10:20:05 of the Chrome session

        // ... and the rest of the shift belongs to the previous local day.
        var early = await _admin.GetJsonAsync<EmployeeDaySummary>($"/api/v1/employees/{_employeeId}/summary?date=2026-03-09&tzOffsetMinutes=-600");
        Assert.Equal(3600, early.LoggedInSeconds);
        Assert.Equal(300, early.IdleSeconds);
        Assert.Equal(0, early.LockedSeconds);
        Assert.Equal(3300, early.ActiveSeconds);
        Assert.Equal(7200, late.LoggedInSeconds + early.LoggedInSeconds);

        // A day the employee never worked.
        var none = await _admin.GetJsonAsync<EmployeeDaySummary>($"/api/v1/employees/{_employeeId}/summary?date=2026-03-12&tzOffsetMinutes=330");
        Assert.Equal(0, none.LoggedInSeconds);
        Assert.Empty(none.Applications);
    }

    [Fact]
    public async Task Device_application_and_process_views_agree_with_the_summary()
    {
        var apps = await _admin.GetJsonAsync<ApplicationActivityResponse>($"/api/v1/devices/{_device.DeviceId}/applications?{Range}");
        Assert.Equal(2, apps.Totals.Count);
        Assert.Equal(3, apps.Sessions.Total);   // Excel A, Chrome B, Excel C: sessions, not samples
        Assert.All(apps.Sessions.Items, s => Assert.Equal("FOREGROUND", s.SessionType));
        Assert.Equal(1800, apps.Sessions.Items.Single(s => s.WindowTitle == "Budget.xlsx" && s.StartedAtUtc == At(9, 0, 5)).DurationSeconds);

        var filtered = await _admin.GetJsonAsync<ApplicationActivityResponse>($"/api/v1/devices/{_device.DeviceId}/applications?{Range}&application=chrome");
        Assert.Equal(["chrome"], filtered.Totals.Select(t => t.ProcessName));

        var procs = await _admin.GetJsonAsync<Paged<ProcessSessionDto>>($"/api/v1/devices/{_device.DeviceId}/processes?{Range}");
        Assert.Equal(2, procs.Total);
        Assert.All(procs.Items, p => { Assert.Equal("STOPPED", p.Status); Assert.NotNull(p.DurationSeconds); });
        var running = await _admin.GetJsonAsync<Paged<ProcessSessionDto>>($"/api/v1/devices/{_device.DeviceId}/processes?{Range}&status=RUNNING");
        Assert.Equal(0, running.Total);
        var search = await _admin.GetJsonAsync<Paged<ProcessSessionDto>>($"/api/v1/devices/{_device.DeviceId}/processes?{Range}&search=excel");
        Assert.Equal("EXCEL.EXE", search.Items.Single().ProcessName);

        var byCategory = await _admin.GetJsonAsync<ApplicationActivityResponse>($"/api/v1/applications?{Range}&category=Browser&employee=EMP-DAY");
        Assert.Equal(["chrome"], byCategory.Totals.Select(t => t.ProcessName));
        var byDepartment = await _admin.GetJsonAsync<ApplicationActivityResponse>($"/api/v1/applications?{Range}&department=Finance");
        Assert.Equal(2, byDepartment.Totals.Count);
        var none = await _admin.GetJsonAsync<ApplicationActivityResponse>($"/api/v1/applications?{Range}&device=PC-404");
        Assert.Empty(none.Totals);
    }

    [Fact]
    public async Task Timeline_metrics_activity_and_usb_lists_are_populated()
    {
        var timeline = await _admin.GetJsonAsync<List<TimelineItem>>($"/api/v1/devices/{_device.DeviceId}/timeline?{Range}");
        Assert.Equal(timeline.OrderBy(t => t.TimestampUtc).Select(t => t.TimestampUtc), timeline.Select(t => t.TimestampUtc));
        Assert.Equal(2, timeline.Count(t => t.Kind == "LOGIN"));
        Assert.Equal(2, timeline.Count(t => t.Kind == "LOCK"));
        Assert.Equal(3, timeline.Count(t => t.Kind == "APP"));
        Assert.Equal(900, timeline.Single(t => t.Kind == "IDLE").DurationSeconds);
        Assert.Single(timeline, t => t.Kind == "USB");
        Assert.All(timeline, t => Assert.Equal(_device.DeviceId, t.DeviceId));

        var employeeTimeline = await _admin.GetJsonAsync<List<TimelineItem>>($"/api/v1/employees/{_employeeId}/timeline?{Range}");
        Assert.Equal(timeline.Count, employeeTimeline.Count);

        var metrics = await _admin.GetJsonAsync<List<SystemMetricDto>>($"/api/v1/devices/{_device.DeviceId}/metrics?{Range}");
        Assert.Equal([20, 35, 5], metrics.Select(m => m.CpuPercent));
        var sampled = await _admin.GetJsonAsync<List<SystemMetricDto>>($"/api/v1/devices/{_device.DeviceId}/metrics?{Range}&maxPoints=10");
        Assert.InRange(sampled.Count, 1, 10);

        var activity = await _admin.GetJsonAsync<Paged<EventDto>>($"/api/v1/devices/{_device.DeviceId}/activity?{Range}&eventType=APPLICATION_FOREGROUND&eventType=USB_CONNECTED");
        Assert.Equal(4, activity.Total);
        Assert.All(activity.Items, e => Assert.Contains(e.EventType, new[] { "APPLICATION_FOREGROUND", "USB_CONNECTED" }));

        var usb = await _admin.GetJsonAsync<Paged<UsbEventDto>>($"/api/v1/usb-events?{Range}&employee=EMP-DAY");
        Assert.Equal("Backup Stick", usb.Items.Single().Product);
    }

    [Fact]
    public async Task Reports_contain_the_same_numbers()
    {
        var daily = await _admin.GetJsonAsync<ReportResult>("/api/v1/reports/daily?date=2026-03-10&tzOffsetMinutes=0");
        Assert.Equal("daily", daily.Kind);
        var row = daily.Rows.Single(r => r["employeeCode"]?.ToString() == "EMP-DAY");
        Assert.Equal("7200", row["loggedInSeconds"]!.ToString());
        Assert.Equal("5400", row["activeSeconds"]!.ToString());
        Assert.Equal("chrome", row["topApplication"]!.ToString());

        var usage = await _admin.GetJsonAsync<ReportResult>($"/api/v1/reports/application-usage?{Range}&employee=EMP-DAY");
        Assert.Equal(["application", "category", "runningSeconds", "foregroundSeconds", "interactionSeconds", "launches", "foregroundTransitions"], usage.Columns.Select(c => c.Key));
        Assert.Equal("duration", usage.Columns.Single(c => c.Key == "foregroundSeconds").Type);
        Assert.Equal(2, usage.Rows.Count);

        var activeIdle = await _admin.GetJsonAsync<ReportResult>($"/api/v1/reports/active-idle?{Range}&tzOffsetMinutes=0");
        Assert.Contains(activeIdle.Rows, r => r["employeeCode"]?.ToString() == "EMP-DAY" && r["idleSeconds"]?.ToString() == "900");

        var department = await _admin.GetJsonAsync<ReportResult>($"/api/v1/reports/department-summary?{Range}");
        Assert.Contains(department.Rows, r => r["department"]?.ToString() == "Finance" && r["activeSeconds"]?.ToString() == "5400");

        var timeline = await _admin.GetJsonAsync<ReportResult>($"/api/v1/reports/process-timeline?{Range}&device={_device.DeviceId}");
        Assert.Equal(2, timeline.Rows.Count);
        var usbReport = await _admin.GetJsonAsync<ReportResult>($"/api/v1/reports/usb?{Range}");
        Assert.Contains(usbReport.Rows, r => r["product"]?.ToString() == "Backup Stick");
    }

    [Fact]
    public async Task Every_report_kind_returns_json_and_csv()
    {
        foreach (var kind in new[] { "daily", "application-usage", "active-idle", "device-health", "process-timeline", "usb", "agent-health", "department-summary" })
        {
            var json = await _admin.GetAsync($"/api/v1/reports/{kind}?{Range}");
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            var report = await json.ReadAsync<ReportResult>();
            Assert.Equal(kind, report.Kind);
            Assert.NotEmpty(report.Columns);

            var csv = await _admin.GetAsync($"/api/v1/reports/{kind}?{Range}&format=csv");
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.StartsWith("text/csv", csv.Content.Headers.ContentType!.MediaType);
            var text = await csv.Content.ReadAsStringAsync();
            Assert.Equal(string.Join(',', report.Columns.Select(c => c.Label)), text.Split('\n')[0].TrimEnd('\r'));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/v1/reports/not-a-report")).StatusCode);
    }

    [Fact]
    public async Task Device_health_and_agent_health_reports_expose_the_columns_the_dashboard_looks_for()
    {
        var health = await _admin.GetJsonAsync<ReportResult>($"/api/v1/reports/device-health?{Range}");
        Assert.Contains(health.Columns, c => c.Key.Contains("device", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(health.Columns, c => c.Key.Contains("disk", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(health.Columns, c => c.Key.Contains("queue", StringComparison.OrdinalIgnoreCase));
        var mine = health.Rows.Single(r => r["device"]?.ToString() == _device.DeviceId);
        Assert.Equal("61", mine["diskUsedPercent"]!.ToString());
        Assert.Equal("20", mine["avgCpuPercent"]?.ToString());   // (20 + 35 + 5) / 3
    }

    [Fact]
    public async Task Device_and_employee_lists_and_detail_reflect_the_ingested_data()
    {
        var detail = await _admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{_device.DeviceId}");
        Assert.Equal("EMP-DAY", detail.Summary.EmployeeCode);
        Assert.Equal("Finance", detail.Summary.Department);
        Assert.Contains("dana", detail.WindowsUsers);
        Assert.Contains("10.0.0.5", detail.IpAddresses);
        Assert.Contains("AA-BB-CC-DD-EE-FF", detail.MacAddresses);
        Assert.NotNull(detail.LastBootUtc);

        var list = await _admin.GetJsonAsync<Paged<DeviceSummary>>("/api/v1/devices?employee=EMP-DAY&pageSize=10");
        Assert.Equal(_device.DeviceId, list.Items.Single().DeviceId);
        Assert.Equal(1, (await _admin.GetJsonAsync<Paged<DeviceSummary>>("/api/v1/devices?search=dana")).Total);
        Assert.Equal(0, (await _admin.GetJsonAsync<Paged<DeviceSummary>>("/api/v1/devices?status=ONLINE&employee=EMP-DAY")).Total);

        var employee = await _admin.GetJsonAsync<EmployeeDto>($"/api/v1/employees/{_employeeId}");
        Assert.Equal(1, employee.DeviceCount);
        Assert.Contains("Finance", await _admin.GetJsonAsync<List<string>>("/api/v1/departments"));

        var overview = await _admin.GetJsonAsync<OverviewDto>("/api/v1/overview");
        Assert.True(overview.TotalDevices >= 1);
        Assert.Equal(24, overview.ActivityByHour.Count);
        Assert.True(overview.TotalEmployees >= 1);
    }
}
