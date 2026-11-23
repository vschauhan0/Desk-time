/** Presentation helpers. Pure functions, no React, no globals except Intl. */

const DASH = "—";

/** "2h 18m", "12m 5s", "45s", "1d 3h". Null/NaN -> em dash. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return DASH;
  const total = Math.max(0, Math.round(seconds));
  if (total < 60) return `${total}s`;
  const days = Math.floor(total / 86400);
  const hours = Math.floor((total % 86400) / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = total % 60;
  if (days > 0) return hours > 0 ? `${days}d ${hours}h` : `${days}d`;
  if (hours > 0) return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
  return secs > 0 ? `${minutes}m ${secs}s` : `${minutes}m`;
}

/** Percentage with one decimal below 10, integer above. Input is already 0..100. */
export function formatPercent(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return DASH;
  return value < 10 ? `${value.toFixed(1)}%` : `${Math.round(value)}%`;
}

export function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return DASH;
  return new Intl.NumberFormat("en-US").format(value);
}

/** Megabytes -> "512 MB" / "3.2 GB". */
export function formatMegabytes(mb: number | null | undefined): string {
  if (mb === null || mb === undefined || !Number.isFinite(mb)) return DASH;
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
}

export function formatGigabytes(gb: number | null | undefined): string {
  if (gb === null || gb === undefined || !Number.isFinite(gb)) return DASH;
  return gb >= 1024 ? `${(gb / 1024).toFixed(1)} TB` : `${gb.toFixed(gb < 10 ? 1 : 0)} GB`;
}

function toDate(value: string | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === "") return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** "8 seconds ago", "3 minutes ago", "2 hours ago", "1 day ago". */
export function formatRelative(value: string | Date | null | undefined, now: number = Date.now()): string {
  const d = toDate(value);
  if (!d) return "never";
  const diff = Math.round((now - d.getTime()) / 1000);
  if (diff < -5) return "in the future";
  const s = Math.max(0, diff);
  const unit = (n: number, name: string) => `${n} ${name}${n === 1 ? "" : "s"} ago`;
  if (s < 60) return unit(s, "second");
  if (s < 3600) return unit(Math.floor(s / 60), "minute");
  if (s < 86400) return unit(Math.floor(s / 3600), "hour");
  return unit(Math.floor(s / 86400), "day");
}

/** Local date+time in the viewer's timezone, e.g. "21 Sep 2026, 15:05:20". */
export function formatDateTime(value: string | Date | null | undefined): string {
  const d = toDate(value);
  if (!d) return DASH;
  return new Intl.DateTimeFormat("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(d);
}

export function formatTime(value: string | Date | null | undefined, withSeconds = true): string {
  const d = toDate(value);
  if (!d) return DASH;
  return new Intl.DateTimeFormat("en-GB", {
    hour: "2-digit",
    minute: "2-digit",
    second: withSeconds ? "2-digit" : undefined,
    hour12: false,
  }).format(d);
}

export function formatDate(value: string | Date | null | undefined): string {
  const d = toDate(value);
  if (!d) return DASH;
  return new Intl.DateTimeFormat("en-GB", { day: "2-digit", month: "short", year: "numeric" }).format(d);
}

/** "2026-09-21 09:35:20 UTC" - used as tooltip. */
export function formatUtc(value: string | Date | null | undefined): string {
  const d = toDate(value);
  if (!d) return "";
  return `${d.toISOString().slice(0, 19).replace("T", " ")} UTC`;
}

/** yyyy-MM-dd of a Date in the viewer's local timezone (value for <input type="date">). */
export function toDateInput(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

/** Parse yyyy-MM-dd as local midnight. Returns null when invalid. */
export function parseDateInput(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return null;
  const d = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Minutes east of UTC for the viewer's zone at a given date. Matches the API's `tzOffsetMinutes`. */
export function localOffsetMinutes(d: Date = new Date()): number {
  const off = -d.getTimezoneOffset();
  return off === 0 ? 0 : off;
}

/** "SERVICE_STOPPED" -> "Service stopped". */
export function humanize(value: string | null | undefined): string {
  if (!value) return DASH;
  const spaced = value.replace(/_/g, " ").toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** Share of `part` in `whole` as 0..100 (0 when whole is 0). */
export function ratio(part: number, whole: number): number {
  if (!Number.isFinite(part) || !Number.isFinite(whole) || whole <= 0) return 0;
  return Math.min(100, Math.max(0, (part / whole) * 100));
}

/** Compare dotted version strings ("1.10.0" > "1.9.3"). Non-numeric parts compare as 0. */
export function compareVersions(a: string, b: string): number {
  const pa = a.split(".").map((p) => Number.parseInt(p, 10) || 0);
  const pb = b.split(".").map((p) => Number.parseInt(p, 10) || 0);
  const len = Math.max(pa.length, pb.length);
  for (let i = 0; i < len; i++) {
    const diff = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (diff !== 0) return diff > 0 ? 1 : -1;
  }
  return 0;
}

/** Render an unknown JSON cell (report row) as text. */
export function formatCell(value: unknown, type?: string): string {
  if (value === null || value === undefined) return DASH;
  if (typeof value === "number") {
    if (type === "duration") return formatDuration(value);
    if (type === "percent") return formatPercent(value);
    return formatNumber(value);
  }
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (typeof value === "string") {
    if (type === "datetime" || type === "timestamp") return formatDateTime(value);
    return value;
  }
  return JSON.stringify(value);
}
