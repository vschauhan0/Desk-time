import type { Query } from "./api";
import { localOffsetMinutes, parseDateInput, toDateInput } from "./format";

// ----- Date ranges -----------------------------------------------------------------------------

export type DatePreset = "today" | "yesterday" | "last7" | "custom";

export const DATE_PRESETS: { value: DatePreset; label: string }[] = [
  { value: "today", label: "Today" },
  { value: "yesterday", label: "Yesterday" },
  { value: "last7", label: "Last 7 days" },
  { value: "custom", label: "Custom" },
];

export interface CustomRange {
  /** yyyy-MM-dd, local, inclusive */
  from: string;
  /** yyyy-MM-dd, local, inclusive */
  to: string;
}

export interface DateRange {
  from: Date;
  to: Date;
}

function startOfDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}

function addDays(d: Date, days: number): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate() + days);
}

/**
 * Resolve a preset to a half-open [from, to) range in the viewer's local time zone.
 * Custom ranges are inclusive on both ends at day granularity; invalid custom values fall back to today.
 */
export function resolveDateRange(preset: DatePreset, custom: CustomRange | null, now: Date = new Date()): DateRange {
  const today = startOfDay(now);
  switch (preset) {
    case "yesterday":
      return { from: addDays(today, -1), to: today };
    case "last7":
      return { from: addDays(today, -6), to: addDays(today, 1) };
    case "custom": {
      const from = custom ? parseDateInput(custom.from) : null;
      const to = custom ? parseDateInput(custom.to) : null;
      if (from && to) {
        const [lo, hi] = from <= to ? [from, to] : [to, from];
        return { from: lo, to: addDays(hi, 1) };
      }
      return { from: today, to: addDays(today, 1) };
    }
    case "today":
    default:
      return { from: today, to: addDays(today, 1) };
  }
}

/** Query params `from`/`to` (ISO instants) and `tzOffsetMinutes` for a resolved range. */
export function rangeToQuery(range: DateRange): Query {
  return {
    from: range.from.toISOString(),
    to: range.to.toISOString(),
    tzOffsetMinutes: localOffsetMinutes(range.from),
  };
}

export function defaultCustomRange(now: Date = new Date()): CustomRange {
  const today = toDateInput(now);
  return { from: today, to: today };
}

// ----- Dimension filters -----------------------------------------------------------------------

export type FilterKey =
  | "employee"
  | "department"
  | "device"
  | "application"
  | "process"
  | "eventType"
  | "status"
  | "category"
  | "search";

export type FilterState = Partial<Record<FilterKey, string>>;

/** Drop empty values so that they never reach the query string. */
export function cleanFilters(filters: FilterState): FilterState {
  const out: FilterState = {};
  for (const key of Object.keys(filters) as FilterKey[]) {
    const v = filters[key]?.trim();
    if (v) out[key] = v;
  }
  return out;
}

/**
 * Map UI filters to API query parameters. `mapping` renames UI keys to what the endpoint expects
 * (e.g. process -> search on /processes, eventType -> eventType). Only `allowed` keys are forwarded.
 */
export function filtersToQuery(
  filters: FilterState,
  allowed: readonly FilterKey[],
  mapping: Partial<Record<FilterKey, string>> = {},
): Query {
  const query: Query = {};
  const cleaned = cleanFilters(filters);
  for (const key of allowed) {
    const value = cleaned[key];
    if (value) query[mapping[key] ?? key] = value;
  }
  return query;
}

/** Client-side text match used by the live grid (case-insensitive across the given fields). */
export function matchesSearch(search: string | undefined, ...fields: (string | null | undefined)[]): boolean {
  const q = search?.trim().toLowerCase();
  if (!q) return true;
  return fields.some((f) => f?.toLowerCase().includes(q));
}

/** Client-side filter for live cards (they are patched in real time, so the server is not re-queried). */
export function filterLiveCards<
  T extends {
    status: string;
    department: string | null;
    employeeCode: string | null;
    employeeName: string | null;
    hostname: string;
    deviceId: string;
    currentApplication: string | null;
  },
>(cards: readonly T[], filters: FilterState): T[] {
  const f = cleanFilters(filters);
  return cards.filter(
    (c) =>
      (!f.status || c.status === f.status) &&
      (!f.department || c.department === f.department) &&
      (!f.employee || c.employeeCode === f.employee) &&
      matchesSearch(f.search, c.hostname, c.deviceId, c.employeeName, c.employeeCode, c.currentApplication, c.department),
  );
}
