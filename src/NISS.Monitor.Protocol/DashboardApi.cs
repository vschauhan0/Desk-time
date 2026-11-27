using System.Text.Json;

namespace NISS.Monitor.Protocol;

// ---------------------------------------------------------------------------------------------
// Dashboard-facing read models. All timestamps are UTC (DateTimeOffset, offset 0) unless the name
// says otherwise. The web dashboard mirrors these in web/niss-monitor-dashboard/src/lib/types.ts.
// All endpoints are under /api/v1 and require a JWT bearer token unless stated.
// ---------------------------------------------------------------------------------------------

/// <summary>Generic page wrapper. Query string: page (1-based, default 1), pageSize (default 50, max 500).</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>One stored screenshot's metadata for the dashboard timeline (the JPEG is fetched separately).</summary>
/// <param name="Trigger">One of <see cref="ScreenTriggers"/>: why the frame was captured.</param>
/// <param name="FlagTerm">The flagged term that matched, for a flagged capture.</param>
/// <param name="MatchedOn">One of <see cref="FlagMatchKinds"/>, for a flagged capture.</param>
/// <param name="ClipId">Groups the frames of one clip; null on a single still.</param>
/// <param name="ClipSeq">0-based position within the clip.</param>
/// <param name="ClipTotal">Frames the clip was expected to contain.</param>
public sealed record ScreenshotDto(
    Guid Id,
    DateTimeOffset CapturedAtUtc,
    int Monitor,
    int Width,
    int Height,
    int ByteSize,
    string? SessionUser,
    Guid? EmployeeId,
    string Trigger = ScreenTriggers.Periodic,
    string? FlagTerm = null,
    string? MatchedOn = null,
    Guid? ClipId = null,
    int ClipSeq = 0,
    int ClipTotal = 0);

/// <summary>
/// One flagged-activity clip: its frames in order. A clip is a burst of JPEG stills the dashboard replays, not an
/// encoded video, so each frame is fetched through the ordinary screenshot image endpoint.
/// </summary>
public sealed record ScreenshotClipDto(
    Guid ClipId,
    DateTimeOffset StartedAtUtc,
    string? FlagTerm,
    string? MatchedOn,
    int FrameCount,
    int ExpectedFrameCount,
    IReadOnlyList<ScreenshotDto> Frames);

/// <summary>A started live-view session: the SignalR group the dashboard joins to receive frames.</summary>
public sealed record LiveViewSessionDto(Guid LiveSessionId, string DeviceId, string HubGroup, int Monitor, DateTimeOffset StartedAtUtc);

// ----- Auth -----------------------------------------------------------------------------------

/// <summary>POST /auth/login (anonymous, rate limited).</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>POST /auth/refresh (anonymous).</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Signed-in user.</summary>
/// <summary>
/// Signed-in user. <paramref name="PageAccess"/> maps page/sub-page key -&gt; <see cref="PageAccess"/> level; null =
/// EDIT on every page the role allows (SUPERADMIN, or an unrestricted user).
/// </summary>
public sealed record UserInfo(Guid Id, string Username, string DisplayName, string Role, IReadOnlyDictionary<string, string>? PageAccess = null);

/// <summary>Login / refresh result.</summary>
public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc, UserInfo User);

/// <summary>Access a user has to one page or sub-page. HIDDEN = not shown; VIEW = read-only; EDIT = can act.</summary>
public static class PageAccess
{
    public const string Hidden = "HIDDEN";
    public const string View = "VIEW";
    public const string Edit = "EDIT";

    public static bool IsLevel(string? v) => v is Hidden or View or Edit;
    /// <summary>EDIT &gt; VIEW &gt; HIDDEN.</summary>
    public static int Rank(string? v) => v switch { Edit => 2, View => 1, _ => 0 };
    public static bool CanView(string? v) => Rank(v) >= 1;
    public static bool CanEdit(string? v) => Rank(v) >= 2;
}

/// <summary>
/// Dashboard user. Access fields are honoured for ADMIN and VIEWER; a SUPERADMIN always has EDIT on every page and
/// sees every employee. <paramref name="PageAccess"/> is a map of page/sub-page key -&gt; <see cref="PageAccess"/>
/// level; null = EDIT on every page the role allows. When <paramref name="ScopeAllEmployees"/> is true the
/// department/employee lists are ignored.
/// </summary>
public sealed record AdminUserDto(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginUtc,
    IReadOnlyDictionary<string, string>? PageAccess = null,
    bool ScopeAllEmployees = true,
    IReadOnlyList<string>? AllowedDepartments = null,
    IReadOnlyList<Guid>? AllowedEmployeeIds = null);

/// <summary>Create/update an admin user. Password required on create, optional on update.</summary>
public sealed record AdminUserUpsert(
    string Username,
    string DisplayName,
    string Role,
    bool Enabled,
    string? Password,
    IReadOnlyDictionary<string, string>? PageAccess = null,
    bool ScopeAllEmployees = true,
    IReadOnlyList<string>? AllowedDepartments = null,
    IReadOnlyList<Guid>? AllowedEmployeeIds = null);

/// <summary>
/// One page (or sub-page/tab) an admin's access can be set on. <paramref name="Parent"/> is the owning page key for a
/// sub-page, else null. <paramref name="Editable"/> is true when the page has actions that EDIT level unlocks.
/// </summary>
public sealed record AppPageInfo(string Key, string Label, string Route, string Group, string MinRole, string? Parent = null, bool Editable = false);

/// <summary>Canonical catalogue of dashboard pages and sub-pages, used for per-user access. Keys are stable; routes mirror the UI.</summary>
public static class AppPages
{
    public static readonly IReadOnlyList<AppPageInfo> All =
    [
        new("overview", "Overview", "/", "Monitor", "VIEWER"),
        new("live", "Live Monitoring", "/live", "Monitor", "VIEWER"),
        new("employees", "Employees", "/employees", "People & devices", "VIEWER", Editable: true),
        new("devices", "Devices", "/devices", "People & devices", "VIEWER", Editable: true),
        // Device-detail tabs (sub-pages of Devices).
        new("device.overview", "· Overview tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.activity", "· Activity tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.applications", "· Applications tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.processes", "· Processes tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.metrics", "· Metrics tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.timeline", "· Timeline tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.diagnostics", "· Diagnostics tab", "/devices", "People & devices", "VIEWER", Parent: "devices"),
        new("device.screens", "· Screens tab (screenshots/live)", "/devices", "People & devices", "SUPERADMIN", Parent: "devices"),
        new("device-health", "Device Health", "/device-health", "People & devices", "VIEWER"),
        new("applications", "Application Activity", "/applications", "Activity", "VIEWER"),
        new("processes", "Process Timeline", "/processes", "Activity", "VIEWER"),
        new("timeline", "Employee Timeline", "/timeline", "Activity", "VIEWER"),
        new("active-idle", "Active / Idle", "/active-idle", "Activity", "VIEWER"),
        new("usb", "USB Events", "/usb", "Events", "VIEWER"),
        new("files", "File Activity", "/files", "Events", "VIEWER"),
        new("security", "Security & Agent Health", "/security", "Events", "VIEWER", Editable: true),
        new("reports", "Reports", "/reports", "Administration", "VIEWER"),
        new("settings", "Settings", "/settings", "Administration", "ADMIN", Editable: true),
        // Settings tabs (sub-pages of Settings).
        new("settings.policies", "· Agent policies", "/settings", "Administration", "ADMIN", Parent: "settings", Editable: true),
        new("settings.categories", "· Application categories", "/settings", "Administration", "ADMIN", Parent: "settings", Editable: true),
        new("settings.retention", "· Retention & storage", "/settings", "Administration", "ADMIN", Parent: "settings", Editable: true),
        new("settings.thresholds", "· Health thresholds", "/settings", "Administration", "ADMIN", Parent: "settings", Editable: true),
        new("settings.tokens", "· Enrollment tokens", "/settings", "Administration", "ADMIN", Parent: "settings", Editable: true),
        new("settings.admins", "· Administrators", "/settings", "Administration", "SUPERADMIN", Parent: "settings", Editable: true),
        new("audit", "Audit Log", "/audit", "Administration", "ADMIN"),
    ];

    public static readonly IReadOnlySet<string> Keys = All.Select(p => p.Key).ToHashSet();
    public static AppPageInfo? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}

// ----- Employees ------------------------------------------------------------------------------

/// <summary>Employee. Presence/status are those of the employee's most recently seen device.</summary>
public sealed record EmployeeDto(
    Guid Id,
    string EmployeeCode,
    string FullName,
    string? Department,
    string? Email,
    bool Active,
    int DeviceCount,
    string? Status,
    string? Presence,
    DateTimeOffset? LastSeenUtc);

/// <summary>POST/PUT /employees body.</summary>
public sealed record EmployeeUpsert(string EmployeeCode, string FullName, string? Department, string? Email, bool Active);

/// <summary>One line of an employee/device timeline. <see cref="Kind"/> is a display grouping (LOGIN, LOCK, APP, IDLE, PROCESS, USB, ...).</summary>
public sealed record TimelineItem(
    DateTimeOffset TimestampUtc,
    int LocalOffsetMinutes,
    string EventType,
    string Kind,
    string Title,
    string? Detail,
    string DeviceId,
    string? ProcessName,
    double? DurationSeconds);

/// <summary>GET /employees/{id}/summary?date=yyyy-MM-dd (date interpreted in the tz offset given by tzOffsetMinutes, default server local).</summary>
public sealed record EmployeeDaySummary(
    EmployeeDto Employee,
    string Date,
    DateTimeOffset? FirstLoginUtc,
    DateTimeOffset? LastLogoutUtc,
    bool LoggedInNow,
    double LoggedInSeconds,
    double ActiveSeconds,
    double IdleSeconds,
    double LockedSeconds,
    IReadOnlyList<AppUsageTotal> Applications,
    IReadOnlyList<string> Devices);

/// <summary>
/// Per-application totals. Deliberately separates OPEN/RUNNING (<see cref="RunningSeconds"/>) from
/// ACTIVELY USED (<see cref="InteractionSeconds"/>) and FOREGROUND (<see cref="ForegroundSeconds"/>).
/// </summary>
public sealed record AppUsageTotal(
    string ProcessName,
    string Category,
    double RunningSeconds,
    double ForegroundSeconds,
    double InteractionSeconds,
    int Launches,
    int ForegroundTransitions);

// ----- Devices --------------------------------------------------------------------------------

/// <summary>Row in GET /devices. Query: status, employee (code), department, search, page, pageSize.</summary>
public sealed record DeviceSummary(
    Guid Id,
    string DeviceId,
    string Hostname,
    string? EmployeeCode,
    string? EmployeeName,
    string? Department,
    string? OperatingSystem,
    string AgentVersion,
    string Status,
    string Presence,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset CreatedAtUtc,
    string? CurrentApplication,
    string? CurrentWindow,
    double? CpuPercent,
    double? MemoryUsedPercent);

/// <summary>A running application with the three separate concepts: running, foreground, interaction.</summary>
public sealed record AppState(
    string ProcessName,
    int ProcessId,
    string? WindowTitle,
    DateTimeOffset? StartedUtc,
    bool Running,
    bool Foreground,
    bool Interaction);

/// <summary>Live Monitoring card; also pushed over SignalR as DeviceStateChanged.</summary>
public sealed record LiveDeviceCard(
    string DeviceId,
    string Hostname,
    string? EmployeeCode,
    string? EmployeeName,
    string? Department,
    string Status,
    string Presence,
    bool IsIdle,
    bool IsLocked,
    bool IsInteracting,
    string? CurrentApplication,
    string? CurrentWindow,
    IReadOnlyList<AppState> Applications,
    double? CpuPercent,
    double? MemoryUsedPercent,
    bool? NetworkConnected,
    DateTimeOffset? LastHeartbeatUtc,
    string AgentVersion,
    string? UserName);

/// <summary>GET /devices/{deviceId}.</summary>
/// <summary>One installed program in a device's inventory.</summary>
public sealed record InstalledSoftwareDto(string Name, string? Version, string? Publisher);

/// <summary>Latest hardware + installed-software snapshot for a device (dashboard read model).</summary>
public sealed record DeviceInventoryDto(
    string DeviceId,
    DateTimeOffset? CapturedAtUtc,
    string? Manufacturer,
    string? Model,
    string? Serial,
    string? OsName,
    string? OsVersion,
    string? OsBuild,
    string? CpuModel,
    int? CpuCores,
    int? CpuLogical,
    double? RamGb,
    double? DiskTotalGb,
    IReadOnlyList<InstalledSoftwareDto> Software);

public sealed record DeviceDetail(
    DeviceSummary Summary,
    LiveDeviceCard Live,
    string? MachineSerial,
    string? Manufacturer,
    string? Model,
    string? OsVersion,
    IReadOnlyList<string> WindowsUsers,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> MacAddresses,
    DateTimeOffset? LastBootUtc);

/// <summary>Stored event (GET /devices/{id}/activity). Query: from, to, eventType (repeatable), page, pageSize.</summary>
public sealed record EventDto(
    Guid EventId,
    string EventType,
    DateTimeOffset TimestampUtc,
    int LocalOffsetMinutes,
    string DeviceId,
    string? EmployeeCode,
    string Source,
    JsonElement Payload);

/// <summary>GET /devices/{id}/applications?from&amp;to. Also GET /applications (all devices; adds employee, department, application filters).</summary>
public sealed record ApplicationActivityResponse(
    IReadOnlyList<AppUsageTotal> Totals,
    Paged<ApplicationSessionDto> Sessions);

/// <summary>One foreground application session (time in front) - not one row per sample.</summary>
public sealed record ApplicationSessionDto(
    Guid Id,
    string DeviceId,
    string? EmployeeCode,
    string ProcessName,
    string? ExecutablePath,
    string? WindowTitle,
    string Category,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    double DurationSeconds,
    double InteractionSeconds,
    double IdleSeconds,
    string SessionType);

/// <summary>One process lifetime (GET /devices/{id}/processes?from&amp;to&amp;status&amp;search, also GET /processes).</summary>
public sealed record ProcessSessionDto(
    Guid Id,
    string DeviceId,
    string? EmployeeCode,
    int ProcessId,
    int? ParentProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? UserName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    double? DurationSeconds,
    string Status,
    string? CommandLine);

/// <summary>GET /devices/{id}/metrics?from&amp;to&amp;maxPoints (server down-samples).</summary>
public sealed record SystemMetricDto(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    double MemoryUsedPercent,
    double MemoryAvailableMb,
    double? DiskUsedPercent,
    double? DiskFreeGb,
    bool? NetworkConnected,
    int? BatteryPercent,
    long UptimeSeconds);

/// <summary>USB row. GET /usb-events?from&amp;to&amp;device&amp;employee&amp;department.</summary>
public sealed record UsbEventDto(
    Guid Id,
    string DeviceId,
    string? EmployeeCode,
    string EventType,
    string DeviceType,
    string? Vendor,
    string? Product,
    string? SerialNumber,
    DateTimeOffset TimestampUtc);

/// <summary>File-activity row. GET /file-events?from&amp;to&amp;device&amp;employee&amp;action&amp;search.</summary>
public sealed record FileEventDto(
    Guid Id,
    string DeviceId,
    string? EmployeeCode,
    string Action,
    string Path,
    string FileName,
    string? Extension,
    string? UserName,
    DateTimeOffset TimestampUtc);

/// <summary>Security / agent-health event. GET /security-events?from&amp;to&amp;device&amp;kind&amp;severity&amp;resolved.</summary>
public sealed record SecurityEventDto(
    Guid Id,
    string? DeviceId,
    string Kind,
    string Severity,
    string Message,
    DateTimeOffset RaisedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

/// <summary>Audit log row. GET /audit-logs?from&amp;to&amp;user&amp;action (ADMIN+).</summary>
public sealed record AuditLogDto(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string? Username,
    string Action,
    string? Target,
    string? Detail,
    string? IpAddress);

/// <summary>GET /devices/{id}/diagnostics.</summary>
public sealed record DeviceDiagnostics(
    string DeviceId,
    string Hostname,
    string AgentVersion,
    string ServiceState,
    string SessionAgentState,
    IReadOnlyList<SessionAgentInfo> SessionAgents,
    DateTimeOffset? LastHeartbeatUtc,
    bool ServerConnection,
    int QueueCount,
    int DeadLetterCount,
    DateTimeOffset? LastSuccessfulUploadUtc,
    string? LastError,
    double? CpuPercent,
    double? MemoryUsedPercent,
    double? DiskUsedPercent,
    string? OsVersion,
    IReadOnlyList<CollectorStatus> Collectors,
    long ClockOffsetMs,
    IReadOnlyList<SecurityEventDto> RecentIssues);

// ----- Overview / reports ----------------------------------------------------------------------

/// <summary>Hourly activity bucket for the overview chart.</summary>
public sealed record HourlyActivity(int Hour, double ActiveSeconds, double IdleSeconds);

/// <summary>Version histogram entry.</summary>
public sealed record VersionCount(string Version, int Count);

/// <summary>
/// One recent flagged-activity capture, summarised for the dashboard. Carries no image - the Screens tab
/// (SUPERADMIN) serves the actual JPEG. <paramref name="FlagTerm"/> is the configured site/app name that matched.
/// </summary>
public sealed record FlaggedCaptureDto(
    Guid Id,
    string DeviceId,
    string? EmployeeName,
    DateTimeOffset CapturedAtUtc,
    string? FlagTerm,
    string? MatchedOn,
    Guid? ClipId);

/// <summary>GET /overview.</summary>
/// <param name="FlaggedToday">Count of flagged-activity captures across the fleet today.</param>
/// <param name="RecentFlagged">The most recent flagged captures (one row per clip), newest first.</param>
public sealed record OverviewDto(
    int TotalDevices,
    int Online,
    int Warning,
    int Offline,
    int Disabled,
    int ActiveNow,
    int IdleNow,
    int LockedNow,
    int TotalEmployees,
    long EventsToday,
    int OpenAlerts,
    IReadOnlyList<AppUsageTotal> TopApplicationsToday,
    IReadOnlyList<HourlyActivity> ActivityByHour,
    IReadOnlyList<SecurityEventDto> RecentSecurityEvents,
    IReadOnlyList<VersionCount> AgentVersions,
    int FlaggedToday = 0,
    IReadOnlyList<FlaggedCaptureDto>? RecentFlagged = null);

/// <summary>Column descriptor for <see cref="ReportResult"/>.</summary>
public sealed record ReportColumn(string Key, string Label, string Type);

/// <summary>
/// Generic tabular report. GET /reports/{kind} with kind = daily | application-usage | active-idle |
/// device-health | process-timeline | usb | agent-health | department-summary. Query: from, to, employee,
/// department, device, tzOffsetMinutes, format=json|csv. /reports/daily also accepts date.
/// Reports show objective telemetry only - there is no "productivity" score.
/// </summary>
public sealed record ReportResult(
    string Kind,
    string Title,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<Dictionary<string, object?>> Rows);

// ----- Settings / enrollment -------------------------------------------------------------------

/// <summary>Configuration policy. Scope: GLOBAL (exactly one), DEPARTMENT or DEVICE. Most specific wins per field-set.</summary>
public sealed record PolicyDto(Guid Id, string Name, string Scope, string? ScopeValue, AgentPolicy Policy, DateTimeOffset UpdatedAtUtc);

/// <summary>PUT /settings/policies/{id}.</summary>
public sealed record PolicyUpsert(string Name, string Scope, string? ScopeValue, AgentPolicy Policy);

/// <summary>Application categorisation rule. Category: Developer, Office, Communication, Browser, Media, Utilities, System, Unknown.</summary>
public sealed record AppCategoryRule(Guid Id, string Pattern, string Category, int Priority);

/// <summary>Retention settings in days (0 = keep forever).</summary>
/// <param name="ScreenshotDays">
/// Periodic screenshots. Null means "fall back to the <c>ScreenshotStorage:RetentionDays</c> server setting", which is
/// what a settings row written before screenshot retention was configurable here deserialises to.
/// </param>
/// <param name="FlaggedScreenshotDays">
/// Flagged screenshots and clips, kept on their own (normally longer) schedule because they are the evidence an
/// administrator comes back to. Null falls back to <c>ScreenshotStorage:FlaggedRetentionDays</c>.
/// </param>
public sealed record RetentionSettings(
    int RawEventsDays,
    int ApplicationSummariesDays,
    int DeviceHealthDays,
    int AuditLogsDays,
    int? ScreenshotDays = null,
    int? FlaggedScreenshotDays = null);

/// <summary>Server-side monitoring thresholds (seconds unless noted).</summary>
public sealed record HealthThresholds(int HeartbeatWarningSeconds, int OfflineSeconds, int BacklogWarningThreshold, string? MinAgentVersion);

/// <summary>
/// Where periodic screenshots and flagged-activity recordings are written on the server.
/// <paramref name="Root"/> empty = the server default (<c>ScreenshotStorage:Root</c>, or a temp folder). Changing it
/// affects new captures only; existing files stay under the folder they were written to.
/// </summary>
public sealed record ScreenshotStorageSettings(string Root);

/// <summary>POST /enrollment-tokens (ADMIN+). Returned token is shown once.</summary>
public sealed record EnrollmentTokenRequest(string? EmployeeCode, string? DeviceId, int ExpiresInHours, string? Note);

/// <summary>Enrollment token metadata; Token is only populated on creation.</summary>
public sealed record EnrollmentTokenDto(
    Guid Id,
    string? Token,
    string? EmployeeCode,
    string? DeviceId,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? UsedAtUtc,
    string? UsedByDeviceId,
    string? Note,
    DateTimeOffset CreatedAtUtc);

/// <summary>GET /health (anonymous).</summary>
public sealed record HealthResponse(string Status, string Version, DateTimeOffset ServerTimeUtc, bool Database);

/// <summary>
/// SignalR hub at /hubs/monitor (JWT via access_token query string). Server-to-client methods:
/// DeviceStateChanged(LiveDeviceCard), EventIngested(EventDto), DeviceStatusChanged(DeviceStatusChange),
/// SecurityEventRaised(SecurityEventDto). Client-to-server: SubscribeDevice(deviceId), UnsubscribeDevice(deviceId).
/// Every authenticated client is in group "all" and receives DeviceStateChanged/DeviceStatusChanged/SecurityEventRaised;
/// EventIngested (non-metric events) is sent to group "device:{deviceId}" and to group "feed".
/// </summary>
public sealed record DeviceStatusChange(string DeviceId, string Status, string? PreviousStatus, string? Reason, DateTimeOffset AtUtc);

// ---- Agent auto-update (dashboard) ----

/// <summary>An uploaded, verified agent update package.</summary>
public sealed record AgentPackageDto(
    string Version,
    long SizeBytes,
    string Sha256,
    string? SignerSubject,
    string? UploadedBy,
    DateTimeOffset UploadedAtUtc,
    string? Notes);

/// <summary>A rollout of one package version, with per-device outcome counts.</summary>
public sealed record AgentRolloutDto(
    string Id,
    string Version,
    string State,
    DateTimeOffset CreatedAtUtc,
    int Canary,
    int Updated,
    int Failed,
    int Pending,
    bool GateOpen,
    string? GateReason);

/// <summary>Update state of one device.</summary>
public sealed record DeviceUpdateStateDto(
    string DeviceId,
    string? AssignedVersion,
    string Phase,
    string? LastError,
    string? FailedVersion,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>POST a new rollout for an uploaded package version.</summary>
public sealed record CreateRolloutRequest(string Version, string? Notes);

/// <summary>Devices chosen as the canary group of a rollout.</summary>
public sealed record CanaryRequest(IReadOnlyList<string> DeviceIds);

/// <summary>Names of the SignalR client methods, shared so that nobody mistypes them.</summary>
public static class HubMethods
{
    public const string DeviceStateChanged = "DeviceStateChanged";
    public const string EventIngested = "EventIngested";
    public const string DeviceStatusChanged = "DeviceStatusChanged";
    public const string SecurityEventRaised = "SecurityEventRaised";
    public const string ScreenFrame = "ScreenFrame";
    public const string Route = "/hubs/monitor";
}

/// <summary>One live-view frame pushed to a watching dashboard over SignalR (JPEG as base64).</summary>
public sealed record ScreenFrameMessage(Guid LiveSessionId, int Monitor, string JpegBase64);
