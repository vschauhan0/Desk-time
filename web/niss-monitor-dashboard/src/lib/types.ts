/**
 * TypeScript mirror of the C# contract in src/NISS.Monitor.Protocol
 * (DashboardApi.cs, AgentApi.cs, Payloads.cs, EventTypes.cs).
 *
 * Conventions: camelCase property names, enums travel as strings, DateTimeOffset -> ISO-8601 string,
 * nullable C# members -> `T | null`. Additive server changes must be mirrored here.
 */

export type IsoString = string;

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/** System.Text.Json JsonElement. */
export type JsonElement = JsonValue;

// ----- Generic -------------------------------------------------------------------------------

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** RFC 7807 problem details as returned by the API. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
  traceId?: string;
}

// ----- Auth ----------------------------------------------------------------------------------

export interface LoginRequest {
  username: string;
  password: string;
}

export interface RefreshRequest {
  refreshToken: string;
}

export type Role = "SUPERADMIN" | "ADMIN" | "VIEWER";

export interface UserInfo {
  id: string;
  username: string;
  displayName: string;
  role: Role;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: IsoString;
  user: UserInfo;
}

export interface AdminUserDto {
  id: string;
  username: string;
  displayName: string;
  role: Role;
  enabled: boolean;
  createdAtUtc: IsoString;
  lastLoginUtc: IsoString | null;
}

export interface AdminUserUpsert {
  username: string;
  displayName: string;
  role: Role;
  enabled: boolean;
  password: string | null;
}

// ----- Employees -----------------------------------------------------------------------------

export interface EmployeeDto {
  id: string;
  employeeCode: string;
  fullName: string;
  department: string | null;
  email: string | null;
  active: boolean;
  deviceCount: number;
  status: string | null;
  presence: string | null;
  lastSeenUtc: IsoString | null;
}

export interface EmployeeUpsert {
  employeeCode: string;
  fullName: string;
  department: string | null;
  email: string | null;
  active: boolean;
}

export interface TimelineItem {
  timestampUtc: IsoString;
  localOffsetMinutes: number;
  eventType: string;
  kind: string;
  title: string;
  detail: string | null;
  deviceId: string;
  processName: string | null;
  durationSeconds: number | null;
}

/** Deliberately separates OPEN/RUNNING from FOREGROUND from ACTIVELY USED (interaction). */
export interface AppUsageTotal {
  processName: string;
  category: string;
  runningSeconds: number;
  foregroundSeconds: number;
  interactionSeconds: number;
  launches: number;
  foregroundTransitions: number;
}

export interface EmployeeDaySummary {
  employee: EmployeeDto;
  date: string;
  firstLoginUtc: IsoString | null;
  lastLogoutUtc: IsoString | null;
  loggedInNow: boolean;
  loggedInSeconds: number;
  activeSeconds: number;
  idleSeconds: number;
  lockedSeconds: number;
  applications: AppUsageTotal[];
  devices: string[];
}

// ----- Devices -------------------------------------------------------------------------------

export interface DeviceSummary {
  id: string;
  deviceId: string;
  hostname: string;
  employeeCode: string | null;
  employeeName: string | null;
  department: string | null;
  operatingSystem: string | null;
  agentVersion: string;
  status: string;
  presence: string;
  lastSeenUtc: IsoString | null;
  createdAtUtc: IsoString;
  currentApplication: string | null;
  currentWindow: string | null;
  cpuPercent: number | null;
  memoryUsedPercent: number | null;
}

export interface AppState {
  processName: string;
  processId: number;
  windowTitle: string | null;
  startedUtc: IsoString | null;
  running: boolean;
  foreground: boolean;
  interaction: boolean;
}

export interface LiveDeviceCard {
  deviceId: string;
  hostname: string;
  employeeCode: string | null;
  employeeName: string | null;
  department: string | null;
  status: string;
  presence: string;
  isIdle: boolean;
  isLocked: boolean;
  isInteracting: boolean;
  currentApplication: string | null;
  currentWindow: string | null;
  applications: AppState[];
  cpuPercent: number | null;
  memoryUsedPercent: number | null;
  networkConnected: boolean | null;
  lastHeartbeatUtc: IsoString | null;
  agentVersion: string;
  userName: string | null;
}

export interface DeviceDetail {
  summary: DeviceSummary;
  live: LiveDeviceCard;
  machineSerial: string | null;
  manufacturer: string | null;
  model: string | null;
  osVersion: string | null;
  windowsUsers: string[];
  ipAddresses: string[];
  macAddresses: string[];
  lastBootUtc: IsoString | null;
}

export interface EventDto {
  eventId: string;
  eventType: string;
  timestampUtc: IsoString;
  localOffsetMinutes: number;
  deviceId: string;
  employeeCode: string | null;
  source: string;
  payload: JsonElement;
}

export interface ApplicationSessionDto {
  id: string;
  deviceId: string;
  employeeCode: string | null;
  processName: string;
  executablePath: string | null;
  windowTitle: string | null;
  category: string;
  startedAtUtc: IsoString;
  endedAtUtc: IsoString | null;
  durationSeconds: number;
  interactionSeconds: number;
  idleSeconds: number;
  sessionType: string;
}

export interface ApplicationActivityResponse {
  totals: AppUsageTotal[];
  sessions: Paged<ApplicationSessionDto>;
}

export interface ProcessSessionDto {
  id: string;
  deviceId: string;
  employeeCode: string | null;
  processId: number;
  parentProcessId: number | null;
  processName: string;
  executablePath: string | null;
  userName: string | null;
  startedAtUtc: IsoString;
  endedAtUtc: IsoString | null;
  durationSeconds: number | null;
  status: string;
  commandLine: string | null;
}

export interface SystemMetricDto {
  timestampUtc: IsoString;
  cpuPercent: number;
  memoryUsedPercent: number;
  memoryAvailableMb: number;
  diskUsedPercent: number | null;
  diskFreeGb: number | null;
  networkConnected: boolean | null;
  batteryPercent: number | null;
  uptimeSeconds: number;
}

/** Why a screenshot was captured. */
export type ScreenTrigger = "PERIODIC" | "FLAGGED";

/** Where a flagged term was recognised. */
export type FlagMatchKind = "TITLE" | "PROCESS";

export interface ScreenshotDto {
  id: string;
  capturedAtUtc: IsoString;
  monitor: number;
  width: number;
  height: number;
  byteSize: number;
  sessionUser: string | null;
  employeeId: string | null;
  trigger: ScreenTrigger;
  /** The flagged term that matched, for a flagged capture. */
  flagTerm: string | null;
  matchedOn: FlagMatchKind | null;
  /** Groups the frames of one clip; null on a single still. */
  clipId: string | null;
  clipSeq: number;
  clipTotal: number;
}

/**
 * One flagged-activity clip: a burst of JPEG stills the dashboard replays. There is no encoded video file, so each
 * frame is fetched through the ordinary screenshot image endpoint.
 */
export interface ScreenshotClipDto {
  clipId: string;
  startedAtUtc: IsoString;
  flagTerm: string | null;
  matchedOn: FlagMatchKind | null;
  frameCount: number;
  expectedFrameCount: number;
  frames: ScreenshotDto[];
}

export interface LiveViewSessionDto {
  liveSessionId: string;
  deviceId: string;
  hubGroup: string;
  monitor: number;
  startedAtUtc: IsoString;
}

export interface ScreenFrameMessage {
  liveSessionId: string;
  monitor: number;
  jpegBase64: string;
}

export interface UsbEventDto {
  id: string;
  deviceId: string;
  employeeCode: string | null;
  eventType: string;
  deviceType: string;
  vendor: string | null;
  product: string | null;
  serialNumber: string | null;
  timestampUtc: IsoString;
}

export interface FileEventDto {
  id: string;
  deviceId: string;
  employeeCode: string | null;
  action: string;
  path: string;
  fileName: string;
  extension: string | null;
  userName: string | null;
  timestampUtc: IsoString;
}

export interface SecurityEventDto {
  id: string;
  deviceId: string | null;
  kind: string;
  severity: string;
  message: string;
  raisedAtUtc: IsoString;
  resolvedAtUtc: IsoString | null;
}

export interface AuditLogDto {
  id: string;
  timestampUtc: IsoString;
  username: string | null;
  action: string;
  target: string | null;
  detail: string | null;
  ipAddress: string | null;
}

export interface SessionAgentInfo {
  windowsSessionId: number;
  userName: string | null;
  connected: boolean;
  lastSeenUtc: IsoString | null;
}

export interface CollectorStatus {
  name: string;
  state: string;
  lastError: string | null;
  lastEventUtc: IsoString | null;
}

export interface DeviceDiagnostics {
  deviceId: string;
  hostname: string;
  agentVersion: string;
  serviceState: string;
  sessionAgentState: string;
  sessionAgents: SessionAgentInfo[];
  lastHeartbeatUtc: IsoString | null;
  serverConnection: boolean;
  queueCount: number;
  deadLetterCount: number;
  lastSuccessfulUploadUtc: IsoString | null;
  lastError: string | null;
  cpuPercent: number | null;
  memoryUsedPercent: number | null;
  diskUsedPercent: number | null;
  osVersion: string | null;
  collectors: CollectorStatus[];
  clockOffsetMs: number;
  recentIssues: SecurityEventDto[];
}

// ----- Overview / reports --------------------------------------------------------------------

export interface HourlyActivity {
  hour: number;
  activeSeconds: number;
  idleSeconds: number;
}

export interface VersionCount {
  version: string;
  count: number;
}

export interface OverviewDto {
  totalDevices: number;
  online: number;
  warning: number;
  offline: number;
  disabled: number;
  activeNow: number;
  idleNow: number;
  lockedNow: number;
  totalEmployees: number;
  eventsToday: number;
  openAlerts: number;
  topApplicationsToday: AppUsageTotal[];
  activityByHour: HourlyActivity[];
  recentSecurityEvents: SecurityEventDto[];
  agentVersions: VersionCount[];
}

export interface ReportColumn {
  key: string;
  label: string;
  type: string;
}

export interface ReportResult {
  kind: string;
  title: string;
  fromUtc: IsoString;
  toUtc: IsoString;
  columns: ReportColumn[];
  rows: Record<string, JsonValue>[];
}

export const REPORT_KINDS = [
  "daily",
  "attendance",
  "application-usage",
  "active-idle",
  "device-health",
  "process-timeline",
  "usb",
  "agent-health",
  "department-summary",
] as const;
export type ReportKind = (typeof REPORT_KINDS)[number];

// ----- Settings / enrollment -----------------------------------------------------------------

export interface FileMonitoringPolicy {
  enabled: boolean;
  paths: string[];
  ignoreExtensions: string[];
}

export interface CursorSamplingPolicy {
  enabled: boolean;
  intervalSeconds: number;
}

/**
 * Flagged-activity capture. `terms` are case-insensitive substrings matched against the foreground window title and
 * process name — for a website the admin types the site name, because browsers put the page title in the window
 * title. No URL is read or stored.
 */
export interface FlaggedActivityPolicy {
  enabled: boolean;
  terms: string[];
  /** Minimum gap between captures for the same term. */
  cooldownSeconds: number;
  captureClip: boolean;
  clipSeconds: number;
  clipFps: number;
}

export interface ScreenCapturePolicy {
  /** Periodic stored screenshots. */
  enabled: boolean;
  intervalSeconds: number;
  /** JPEG quality, 1-100. */
  quality: number;
  /** Wide screens are downscaled to this width. */
  maxWidth: number;
  /** On-demand live view (independent of stored screenshots). */
  liveEnabled: boolean;
  flaggedActivity: FlaggedActivityPolicy;
}

export interface AgentPolicy {
  version: number;
  heartbeatSeconds: number;
  metricsSeconds: number;
  foregroundPollMilliseconds: number;
  idleThresholdMinutes: number;
  interactionWindowSeconds: number;
  inputBucketSeconds: number;
  captureWindowTitles: boolean;
  captureCommandLines: boolean;
  computeProcessHashes: boolean;
  /** "Interactive" (user-session processes only) or "All". */
  processMonitoringMode: string;
  processExclusions: string[];
  fileMonitoring: FileMonitoringPolicy;
  cursorSampling: CursorSamplingPolicy;
  screenCapture: ScreenCapturePolicy;
  usbMonitoringEnabled: boolean;
  uploadBatchSize: number;
  uploadIntervalSeconds: number;
  backlogWarningThreshold: number;
  minAgentVersion: string | null;
  latestAgentVersion: string | null;
}

export type PolicyScope = "GLOBAL" | "DEPARTMENT" | "DEVICE";

export interface PolicyDto {
  id: string;
  name: string;
  scope: string;
  scopeValue: string | null;
  policy: AgentPolicy;
  updatedAtUtc: IsoString;
}

export interface PolicyUpsert {
  name: string;
  scope: string;
  scopeValue: string | null;
  policy: AgentPolicy;
}

export interface AppCategoryRule {
  id: string;
  pattern: string;
  category: string;
  priority: number;
}

export interface RetentionSettings {
  rawEventsDays: number;
  applicationSummariesDays: number;
  deviceHealthDays: number;
  auditLogsDays: number;
  /** Periodic screenshots. */
  screenshotDays: number;
  /** Flagged screenshots and clips, normally kept longer because they are the evidence. */
  flaggedScreenshotDays: number;
}

export interface HealthThresholds {
  heartbeatWarningSeconds: number;
  offlineSeconds: number;
  backlogWarningThreshold: number;
  minAgentVersion: string | null;
}

export interface EnrollmentTokenRequest {
  employeeCode: string | null;
  deviceId: string | null;
  expiresInHours: number;
  note: string | null;
}

export interface EnrollmentTokenDto {
  id: string;
  token: string | null;
  employeeCode: string | null;
  deviceId: string | null;
  expiresAtUtc: IsoString | null;
  usedAtUtc: IsoString | null;
  usedByDeviceId: string | null;
  note: string | null;
  createdAtUtc: IsoString;
}

export interface HealthResponse {
  status: string;
  version: string;
  serverTimeUtc: IsoString;
  database: boolean;
}

// ----- SignalR -------------------------------------------------------------------------------

export interface DeviceStatusChange {
  deviceId: string;
  status: string;
  previousStatus: string | null;
  reason: string | null;
  atUtc: IsoString;
}

export const HubMethods = {
  DeviceStateChanged: "DeviceStateChanged",
  EventIngested: "EventIngested",
  DeviceStatusChanged: "DeviceStatusChanged",
  SecurityEventRaised: "SecurityEventRaised",
  ScreenFrame: "ScreenFrame",
  Route: "/hubs/monitor",
} as const;

// ----- Event payloads (Payloads.cs) - used to describe events in feeds ---------------------------

export interface ProcessPayload {
  processSessionId: string;
  processId: number;
  parentProcessId: number | null;
  processName: string;
  executablePath: string | null;
  userName: string | null;
  windowsSessionId: number;
  startedUtc: IsoString;
  endedUtc: IsoString | null;
  commandLine: string | null;
  sha256: string | null;
  baseline: boolean;
}

export interface ApplicationLifecyclePayload {
  processId: number;
  processName: string;
  executablePath: string | null;
  windowTitle: string | null;
  windowsSessionId: number;
  openedUtc: IsoString | null;
  closedUtc: IsoString | null;
}

export interface ApplicationForegroundPayload {
  appSessionId: string;
  processId: number;
  processName: string;
  executablePath: string | null;
  windowTitle: string | null;
  previousProcessName: string | null;
  windowsSessionId: number;
}

export interface ApplicationBackgroundPayload {
  appSessionId: string;
  processId: number;
  processName: string;
  executablePath: string | null;
  windowTitle: string | null;
  startedUtc: IsoString;
  endedUtc: IsoString;
  durationSeconds: number;
  interactionSeconds: number;
  idleSeconds: number;
  endReason: string;
  windowsSessionId: number;
}

export interface SessionStatePayload {
  windowsSessionId: number;
  userName: string | null;
  domain: string | null;
  state: string | null;
  clientName: string | null;
}

export interface IdlePayload {
  windowsSessionId: number;
  userName: string | null;
  idleSinceUtc: IsoString | null;
  idleSeconds: number | null;
  thresholdSeconds: number;
}

export interface UsbPayload {
  deviceType: string;
  vendorId: string | null;
  productId: string | null;
  vendor: string | null;
  product: string | null;
  serialNumber: string | null;
  instanceId: string | null;
  driveLetter: string | null;
  arrivedUtc: IsoString | null;
  removedUtc: IsoString | null;
}

export interface FilePayload {
  path: string;
  fileName: string;
  extension: string | null;
  action: string;
  oldPath: string | null;
  userName: string | null;
}

export interface NetworkPayload {
  connected: boolean;
  ipAddresses: string[];
  primaryAdapter: string | null;
}

export interface AgentPayload {
  agentVersion: string;
  previousVersion: string | null;
  reason: string | null;
  message: string | null;
  component: string | null;
  unexpectedPreviousShutdown: boolean | null;
  lastKnownAliveUtc: IsoString | null;
}

// ----- Well-known string sets (EventTypes.cs) ---------------------------------------------------

export const DEVICE_STATUSES = [
  "PENDING",
  "ONLINE",
  "WARNING",
  "OFFLINE",
  "SERVICE_STOPPED",
  "DISABLED",
  "REVOKED",
] as const;

export const PRESENCES = ["ACTIVE", "IDLE", "LOCKED", "DISCONNECTED", "LOGGED_OUT", "UNKNOWN"] as const;

export const EVENT_TYPES = [
  "PROCESS_STARTED",
  "PROCESS_STOPPED",
  "APPLICATION_STARTED",
  "APPLICATION_STOPPED",
  "APPLICATION_FOREGROUND",
  "APPLICATION_BACKGROUND",
  "USER_LOGIN",
  "USER_LOGOUT",
  "SESSION_LOCK",
  "SESSION_UNLOCK",
  "SESSION_CONNECT",
  "SESSION_DISCONNECT",
  "ACTIVE_STARTED",
  "IDLE_STARTED",
  "IDLE_ENDED",
  "MOUSE_ACTIVITY",
  "KEYBOARD_ACTIVITY",
  "USB_CONNECTED",
  "USB_DISCONNECTED",
  "FILE_CREATED",
  "FILE_MODIFIED",
  "FILE_RENAMED",
  "FILE_DELETED",
  "NETWORK_CONNECTED",
  "NETWORK_DISCONNECTED",
  "AGENT_STARTED",
  "AGENT_STOPPED",
  "AGENT_ERROR",
  "AGENT_VERSION_CHANGED",
] as const;

export const SECURITY_EVENT_KINDS = [
  "AUTH_FAILURE",
  "AGENT_BACKLOG_WARNING",
  "HEARTBEAT_DELAYED",
  "DEVICE_OFFLINE",
  "DEVICE_RECOVERED",
  "SERVICE_STOPPED",
  "TAMPER_SUSPECTED",
  "SESSION_AGENT_MISSING",
  "AGENT_OUTDATED",
  "UPLOAD_FAILURE",
  "UNEXPECTED_SHUTDOWN",
  "DEVICE_REVOKED",
  "CLOCK_SKEW",
  "COLLECTOR_FAILURE",
] as const;

export const APP_CATEGORIES = [
  "Developer",
  "Office",
  "Communication",
  "Browser",
  "Media",
  "Utilities",
  "System",
  "Unknown",
] as const;
