import type { JsonValue, ReportColumn, ReportResult } from "./types";

/**
 * The C# contract defines ReportResult generically (Columns + Rows of object?). The Active/Idle view has to know
 * which columns are the label and the three durations, so it detects them by key/label heuristics and unit:
 * keys ending in Seconds/Minutes/Hours (or column type "duration"/"seconds"/"minutes"/"hours").
 */

export interface ActiveIdleRow {
  name: string;
  activeSeconds: number;
  idleSeconds: number;
  lockedSeconds: number;
}

/** Multiplier turning a column's numeric values into seconds. */
export function columnUnitSeconds(col: ReportColumn): number {
  const probe = `${col.key} ${col.type}`.toLowerCase();
  if (/hour/.test(probe)) return 3600;
  if (/minute|\bmin\b/.test(probe)) return 60;
  return 1;
}

function findColumn(columns: ReportColumn[], pattern: RegExp): ReportColumn | undefined {
  return columns.find((c) => pattern.test(c.key)) ?? columns.find((c) => pattern.test(c.label));
}

function num(value: JsonValue | undefined, factor: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value * factor : 0;
}

/** Extract per-employee active/idle/locked seconds from an `active-idle` report; null if columns are not recognisable. */
export function extractActiveIdle(report: ReportResult): ActiveIdleRow[] | null {
  const { columns, rows } = report;
  const active = findColumn(columns, /active/i);
  const idle = findColumn(columns, /idle/i);
  const locked = findColumn(columns, /lock/i);
  if (!active || !idle) return null;
  const label =
    findColumn(columns, /^(employee|employeeName|fullName|name)$/i) ??
    findColumn(columns, /employee|name/i) ??
    columns.find((c) => c.type === "string" || c.type === "text");
  if (!label) return null;
  return rows.map((row) => {
    const raw = row[label.key];
    return {
      name: typeof raw === "string" || typeof raw === "number" ? String(raw) : "Unknown",
      activeSeconds: num(row[active.key], columnUnitSeconds(active)),
      idleSeconds: num(row[idle.key], columnUnitSeconds(idle)),
      lockedSeconds: locked ? num(row[locked.key], columnUnitSeconds(locked)) : 0,
    };
  });
}

/** Read a cell of a report row by a key regex, for merging reports into other tables. */
export function findKey(columns: ReportColumn[], pattern: RegExp): string | null {
  return columns.find((c) => pattern.test(c.key))?.key ?? null;
}

/** Sort rows by a column (strings locale-compare, numbers numeric, nulls last). */
export function sortRows(rows: Record<string, JsonValue>[], key: string, dir: "asc" | "desc"): Record<string, JsonValue>[] {
  const sign = dir === "asc" ? 1 : -1;
  return [...rows].sort((a, b) => {
    const x = a[key];
    const y = b[key];
    if (x === null || x === undefined) return y === null || y === undefined ? 0 : 1;
    if (y === null || y === undefined) return -1;
    if (typeof x === "number" && typeof y === "number") return (x - y) * sign;
    return String(x).localeCompare(String(y), undefined, { numeric: true }) * sign;
  });
}
