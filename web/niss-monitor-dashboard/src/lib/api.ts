import { getApiBaseUrl } from "./config";
import { tokenStore } from "./tokens";
import type {
  AdminUserDto,
  AdminUserUpsert,
  AppCategoryRule,
  ApplicationActivityResponse,
  AuditLogDto,
  AuthResponse,
  DeviceDetail,
  DeviceDiagnostics,
  DeviceSummary,
  EmployeeDaySummary,
  EmployeeDto,
  EmployeeUpsert,
  EnrollmentTokenDto,
  EnrollmentTokenRequest,
  EventDto,
  FileEventDto,
  HealthResponse,
  HealthThresholds,
  LiveDeviceCard,
  OverviewDto,
  Paged,
  PolicyDto,
  PolicyUpsert,
  ProblemDetails,
  ProcessSessionDto,
  ReportResult,
  RetentionSettings,
  SecurityEventDto,
  SystemMetricDto,
  TimelineItem,
  UsbEventDto,
  UserInfo,
} from "./types";

export const API_PREFIX = "/api/v1";

// ----- Errors ----------------------------------------------------------------------------------

/** HTTP error carrying the parsed RFC 7807 problem details. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string | undefined;
  readonly detail: string | undefined;
  readonly errors: Record<string, string[]> | undefined;

  constructor(status: number, message: string, problem?: ProblemDetails) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.title = problem?.title;
    this.detail = problem?.detail;
    this.errors = problem?.errors;
  }
}

/** The server could not be reached at all (DNS, TLS not accepted, connection refused, CORS...). */
export class NetworkError extends Error {
  readonly baseUrl: string;

  constructor(baseUrl: string, cause?: unknown) {
    super(`Cannot reach NISS server at ${baseUrl}`);
    this.name = "NetworkError";
    this.baseUrl = baseUrl;
    if (cause !== undefined) this.cause = cause;
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}

export function isNetworkError(e: unknown): e is NetworkError {
  return e instanceof NetworkError;
}

/** Human friendly message for any thrown value. */
export function errorMessage(e: unknown): string {
  if (e instanceof ApiError) return e.message;
  if (e instanceof NetworkError) return e.message;
  if (e instanceof Error) return e.message;
  return "Unexpected error";
}

/** Parse an error response body (problem+json or plain text) into an ApiError. */
export async function parseProblem(res: Response): Promise<ApiError> {
  let problem: ProblemDetails | undefined;
  let text = "";
  try {
    text = await res.text();
    const type = res.headers.get("content-type") ?? "";
    if (text && (type.includes("json") || text.trim().startsWith("{"))) {
      const parsed: unknown = JSON.parse(text);
      if (typeof parsed === "object" && parsed !== null) problem = parsed as ProblemDetails;
    }
  } catch {
    /* fall through to text/status */
  }
  let message = problem?.detail ?? problem?.title ?? "";
  if (problem?.errors) {
    const flat = Object.entries(problem.errors)
      .map(([field, msgs]) => `${field}: ${msgs.join(", ")}`)
      .join("; ");
    if (flat) message = message ? `${message} (${flat})` : flat;
  }
  if (!message) message = text && text.length < 300 && !text.startsWith("<") ? text : `HTTP ${res.status} ${res.statusText}`.trim();
  return new ApiError(res.status, message, problem);
}

// ----- Hooks (wired by the AuthProvider) --------------------------------------------------------

interface ApiHooks {
  /** Refresh failed / no session: the UI should go to /login. */
  onAuthLost?: () => void;
  /** Reachability transitions (false = network error, true = any HTTP response). */
  onReachability?: (reachable: boolean, baseUrl: string) => void;
}

let hooks: ApiHooks = {};

export function setApiHooks(next: ApiHooks): void {
  hooks = next;
}

// ----- Query building --------------------------------------------------------------------------

export type QueryValue = string | number | boolean | null | undefined | readonly (string | number)[];
export type Query = Record<string, QueryValue>;

/** Serialise a query object; skips null/undefined/empty strings; arrays repeat the key. */
export function buildQueryString(query?: Query): string {
  if (!query) return "";
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined || value === "") continue;
    if (Array.isArray(value)) {
      for (const v of value as readonly (string | number)[]) params.append(key, String(v));
    } else {
      params.append(key, String(value));
    }
  }
  const qs = params.toString();
  return qs ? `?${qs}` : "";
}

/** Append a query object to a path that may already contain a query string. */
export function buildPath(path: string, query?: Query): string {
  const qs = buildQueryString(query);
  if (!qs) return path;
  return path.includes("?") ? `${path}&${qs.slice(1)}` : `${path}${qs}`;
}

// ----- Core request ----------------------------------------------------------------------------

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  query?: Query;
  body?: unknown;
  signal?: AbortSignal;
  /** false = anonymous endpoint (login, health, refresh). Default true. */
  auth?: boolean;
  headers?: Record<string, string>;
}

let refreshInFlight: Promise<boolean> | null = null;

/**
 * Exchange the refresh token for a new pair (single flight). Resolves false when the session is gone
 * (tokens are cleared); rejects with NetworkError when the server cannot be reached (tokens are kept).
 */
export function refreshSession(): Promise<boolean> {
  if (!refreshInFlight) {
    refreshInFlight = doRefresh().finally(() => {
      refreshInFlight = null;
    });
  }
  return refreshInFlight;
}

async function doRefresh(): Promise<boolean> {
  const refreshToken = tokenStore.getRefresh();
  if (!refreshToken) return false;
  const base = await getApiBaseUrl();
  let res: Response;
  try {
    res = await fetch(`${base}${API_PREFIX}/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ refreshToken }),
    });
  } catch (e) {
    hooks.onReachability?.(false, base);
    throw new NetworkError(base, e);
  }
  hooks.onReachability?.(true, base);
  if (!res.ok) {
    tokenStore.clear();
    return false;
  }
  const auth = (await res.json()) as AuthResponse;
  tokenStore.set(auth);
  return true;
}

/** Returns a currently valid access token (refreshing if it is about to expire), or null. Used by SignalR. */
export async function getValidAccessToken(): Promise<string | null> {
  if (tokenStore.needsRefresh() && tokenStore.getRefresh()) {
    try {
      await refreshSession();
    } catch {
      /* keep whatever we have */
    }
  }
  return tokenStore.getAccess();
}

async function send(base: string, path: string, opts: RequestOptions): Promise<Response> {
  const headers: Record<string, string> = { Accept: "application/json, text/csv;q=0.9", ...opts.headers };
  const token = opts.auth === false ? null : tokenStore.getAccess();
  if (token) headers.Authorization = `Bearer ${token}`;
  const init: RequestInit = { method: opts.method ?? "GET", headers, signal: opts.signal };
  if (opts.body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(opts.body);
  }
  try {
    return await fetch(`${base}${API_PREFIX}${buildPath(path, opts.query)}`, init);
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") throw e;
    hooks.onReachability?.(false, base);
    throw new NetworkError(base, e);
  }
}

/** Fetch with auth, refresh-on-401 and problem-details parsing. Returns the raw Response (2xx only). */
export async function requestRaw(path: string, opts: RequestOptions = {}): Promise<Response> {
  const base = await getApiBaseUrl();
  const authed = opts.auth !== false;

  if (authed && tokenStore.needsRefresh() && tokenStore.getRefresh()) {
    await refreshSession();
  }

  let res = await send(base, path, opts);
  hooks.onReachability?.(true, base);

  if (res.status === 401 && authed) {
    const refreshed = await refreshSession();
    if (refreshed) {
      res = await send(base, path, opts);
    } else {
      tokenStore.clear();
      hooks.onAuthLost?.();
      throw new ApiError(401, "Your session has expired. Please sign in again.");
    }
    if (res.status === 401) {
      tokenStore.clear();
      hooks.onAuthLost?.();
      throw new ApiError(401, "Your session has expired. Please sign in again.");
    }
  }
  if (!res.ok) throw await parseProblem(res);
  return res;
}

/** JSON request. 204 / empty body resolves to `undefined` (typed as T for void endpoints). */
export async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const res = await requestRaw(path, opts);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

export interface DownloadResult {
  blob: Blob;
  filename: string;
}

/** Extract filename from Content-Disposition (RFC 6266, incl. filename*=UTF-8''...). */
export function filenameFromDisposition(header: string | null, fallback: string): string {
  if (!header) return fallback;
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(header);
  if (star?.[1]) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ""));
    } catch {
      /* fall through */
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
  return plain?.[1]?.trim() || fallback;
}

export async function download(path: string, query: Query | undefined, fallbackName: string): Promise<DownloadResult> {
  const res = await requestRaw(path, { query });
  const blob = await res.blob();
  return { blob, filename: filenameFromDisposition(res.headers.get("content-disposition"), fallbackName) };
}

/** Trigger a browser save of a blob. */
export function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

// ----- Typed endpoint catalogue ------------------------------------------------------------------

const enc = encodeURIComponent;

export const api = {
  /** Generic GET used by the SWR fetcher; `pathWithQuery` may include a query string. */
  get: <T>(pathWithQuery: string, signal?: AbortSignal) => request<T>(pathWithQuery, { signal }),

  // auth
  login: (username: string, password: string) =>
    request<AuthResponse>("/auth/login", { method: "POST", body: { username, password }, auth: false }),
  logout: async (): Promise<void> => {
    const refreshToken = tokenStore.getRefresh();
    try {
      await request<void>("/auth/logout", { method: "POST", body: { refreshToken } });
    } catch {
      /* logging out must never fail */
    }
  },
  me: () => request<UserInfo>("/auth/me"),
  health: () => request<HealthResponse>("/health", { auth: false }),

  // admin users (SUPERADMIN)
  adminUsers: () => request<AdminUserDto[]>("/admin-users"),
  createAdminUser: (body: AdminUserUpsert) => request<AdminUserDto>("/admin-users", { method: "POST", body }),
  updateAdminUser: (id: string, body: AdminUserUpsert) =>
    request<AdminUserDto>(`/admin-users/${enc(id)}`, { method: "PUT", body }),
  deleteAdminUser: (id: string) => request<void>(`/admin-users/${enc(id)}`, { method: "DELETE" }),

  // overview & live
  overview: () => request<OverviewDto>("/overview"),
  live: (query?: Query) => request<LiveDeviceCard[]>("/live", { query }),

  // devices
  devices: (query?: Query) => request<Paged<DeviceSummary>>("/devices", { query }),
  device: (deviceId: string) => request<DeviceDetail>(`/devices/${enc(deviceId)}`),
  deviceActivity: (deviceId: string, query?: Query) =>
    request<Paged<EventDto>>(`/devices/${enc(deviceId)}/activity`, { query }),
  deviceApplications: (deviceId: string, query?: Query) =>
    request<ApplicationActivityResponse>(`/devices/${enc(deviceId)}/applications`, { query }),
  deviceProcesses: (deviceId: string, query?: Query) =>
    request<Paged<ProcessSessionDto>>(`/devices/${enc(deviceId)}/processes`, { query }),
  deviceMetrics: (deviceId: string, query?: Query) =>
    request<SystemMetricDto[]>(`/devices/${enc(deviceId)}/metrics`, { query }),
  deviceTimeline: (deviceId: string, query?: Query) =>
    request<TimelineItem[]>(`/devices/${enc(deviceId)}/timeline`, { query }),
  deviceDiagnostics: (deviceId: string) => request<DeviceDiagnostics>(`/devices/${enc(deviceId)}/diagnostics`),
  downloadDiagnostics: (deviceId: string) =>
    download(`/devices/${enc(deviceId)}/diagnostics/download`, undefined, `diagnostics-${deviceId}.json`),
  disableDevice: (deviceId: string) => request<void>(`/devices/${enc(deviceId)}/disable`, { method: "POST" }),
  enableDevice: (deviceId: string) => request<void>(`/devices/${enc(deviceId)}/enable`, { method: "POST" }),
  revokeDevice: (deviceId: string) => request<void>(`/devices/${enc(deviceId)}/revoke`, { method: "POST" }),
  reEnrollDevice: (deviceId: string) =>
    request<EnrollmentTokenDto>(`/devices/${enc(deviceId)}/re-enroll`, { method: "POST" }),

  // cross-device
  applications: (query?: Query) => request<ApplicationActivityResponse>("/applications", { query }),
  processes: (query?: Query) => request<Paged<ProcessSessionDto>>("/processes", { query }),

  // employees
  employees: (query?: Query) => request<Paged<EmployeeDto>>("/employees", { query }),
  createEmployee: (body: EmployeeUpsert) => request<EmployeeDto>("/employees", { method: "POST", body }),
  updateEmployee: (id: string, body: EmployeeUpsert) =>
    request<EmployeeDto>(`/employees/${enc(id)}`, { method: "PUT", body }),
  employeeSummary: (id: string, query?: Query) =>
    request<EmployeeDaySummary>(`/employees/${enc(id)}/summary`, { query }),
  employeeTimeline: (id: string, query?: Query) =>
    request<TimelineItem[]>(`/employees/${enc(id)}/timeline`, { query }),
  departments: () => request<string[]>("/departments"),

  // events
  usbEvents: (query?: Query) => request<Paged<UsbEventDto>>("/usb-events", { query }),
  fileEvents: (query?: Query) => request<Paged<FileEventDto>>("/file-events", { query }),
  securityEvents: (query?: Query) => request<Paged<SecurityEventDto>>("/security-events", { query }),
  resolveSecurityEvent: (id: string) =>
    request<void>(`/security-events/${enc(id)}/resolve`, { method: "POST" }),
  auditLogs: (query?: Query) => request<Paged<AuditLogDto>>("/audit-logs", { query }),

  // reports
  report: (kind: string, query?: Query) =>
    request<ReportResult>(`/reports/${enc(kind)}`, { query: { ...query, format: "json" } }),
  reportCsv: (kind: string, query?: Query) =>
    download(`/reports/${enc(kind)}`, { ...query, format: "csv" }, `${kind}.csv`),

  // settings
  policies: () => request<PolicyDto[]>("/settings/policies"),
  updatePolicy: (id: string, body: PolicyUpsert) =>
    request<PolicyDto>(`/settings/policies/${enc(id)}`, { method: "PUT", body }),
  /** Assumption: PUT on the collection creates a DEPARTMENT/DEVICE scoped policy (the contract lists PUT for both). */
  createPolicy: (body: PolicyUpsert) => request<PolicyDto>("/settings/policies", { method: "PUT", body }),
  appCategories: () => request<AppCategoryRule[]>("/settings/app-categories"),
  saveAppCategories: (rules: AppCategoryRule[]) =>
    request<AppCategoryRule[]>("/settings/app-categories", { method: "PUT", body: rules }),
  retention: () => request<RetentionSettings>("/settings/retention"),
  saveRetention: (body: RetentionSettings) =>
    request<RetentionSettings>("/settings/retention", { method: "PUT", body }),
  healthThresholds: () => request<HealthThresholds>("/settings/health-thresholds"),
  saveHealthThresholds: (body: HealthThresholds) =>
    request<HealthThresholds>("/settings/health-thresholds", { method: "PUT", body }),

  // enrollment tokens
  enrollmentTokens: () => request<EnrollmentTokenDto[]>("/enrollment-tokens"),
  createEnrollmentToken: (body: EnrollmentTokenRequest) =>
    request<EnrollmentTokenDto>("/enrollment-tokens", { method: "POST", body }),
  deleteEnrollmentToken: (id: string) => request<void>(`/enrollment-tokens/${enc(id)}`, { method: "DELETE" }),
};
