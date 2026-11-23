import { describe, expect, it } from "vitest";
import { describeEvent } from "../events";
import { layoutGantt } from "../gantt";
import { DEFAULT_POLICY, validatePolicy } from "../policy";
import { columnUnitSeconds, extractActiveIdle, sortRows } from "../report-utils";
import { roleAtLeast } from "../roles";
import { deviceStatusTone, severityTone } from "../status";
import type { EventDto, ProcessSessionDto, ReportResult } from "../types";

describe("roleAtLeast", () => {
  it("orders SUPERADMIN > ADMIN > VIEWER", () => {
    expect(roleAtLeast("SUPERADMIN", "ADMIN")).toBe(true);
    expect(roleAtLeast("ADMIN", "ADMIN")).toBe(true);
    expect(roleAtLeast("VIEWER", "ADMIN")).toBe(false);
    expect(roleAtLeast("ADMIN", "SUPERADMIN")).toBe(false);
    expect(roleAtLeast(undefined, "VIEWER")).toBe(false);
    expect(roleAtLeast("bogus", "VIEWER")).toBe(false);
  });
});

describe("status tones", () => {
  it("maps device status", () => {
    expect(deviceStatusTone("ONLINE")).toBe("ok");
    expect(deviceStatusTone("WARNING")).toBe("warn");
    expect(deviceStatusTone("OFFLINE")).toBe("danger");
    expect(deviceStatusTone("DISABLED")).toBe("neutral");
  });
  it("maps severities case-insensitively", () => {
    expect(severityTone("Critical")).toBe("danger");
    expect(severityTone("warning")).toBe("warn");
    expect(severityTone("INFO")).toBe("info");
    expect(severityTone("weird")).toBe("neutral");
  });
});

describe("report utils", () => {
  const report: ReportResult = {
    kind: "active-idle",
    title: "t",
    fromUtc: "2026-09-21T00:00:00Z",
    toUtc: "2026-09-22T00:00:00Z",
    columns: [
      { key: "employee", label: "Employee", type: "string" },
      { key: "activeSeconds", label: "Active", type: "duration" },
      { key: "idleSeconds", label: "Idle", type: "duration" },
      { key: "lockedSeconds", label: "Locked", type: "duration" },
    ],
    rows: [
      { employee: "Asha", activeSeconds: 3600, idleSeconds: 600, lockedSeconds: 60 },
      { employee: "Ben", activeSeconds: 7200, idleSeconds: null, lockedSeconds: 0 },
    ],
  };
  it("extracts active/idle/locked rows", () => {
    expect(extractActiveIdle(report)).toEqual([
      { name: "Asha", activeSeconds: 3600, idleSeconds: 600, lockedSeconds: 60 },
      { name: "Ben", activeSeconds: 7200, idleSeconds: 0, lockedSeconds: 0 },
    ]);
  });
  it("returns null when the columns are unrecognisable", () => {
    expect(extractActiveIdle({ ...report, columns: [{ key: "x", label: "X", type: "string" }] })).toBeNull();
  });
  it("detects minute/hour units", () => {
    expect(columnUnitSeconds({ key: "activeMinutes", label: "Active", type: "number" })).toBe(60);
    expect(columnUnitSeconds({ key: "activeHours", label: "Active", type: "number" })).toBe(3600);
    expect(columnUnitSeconds({ key: "activeSeconds", label: "Active", type: "duration" })).toBe(1);
  });
  it("sorts numbers and strings, nulls last", () => {
    const rows = [{ a: 2 }, { a: null }, { a: 10 }, { a: 1 }];
    expect(sortRows(rows, "a", "asc").map((r) => r.a)).toEqual([1, 2, 10, null]);
    expect(sortRows(rows, "a", "desc").map((r) => r.a)).toEqual([10, 2, 1, null]);
  });
});

describe("layoutGantt", () => {
  const mk = (id: string, start: string, end: string | null): ProcessSessionDto => ({
    id,
    deviceId: "PC",
    employeeCode: null,
    processId: 1,
    parentProcessId: null,
    processName: "p",
    executablePath: null,
    userName: null,
    startedAtUtc: start,
    endedAtUtc: end,
    durationSeconds: null,
    status: end ? "STOPPED" : "RUNNING",
    commandLine: null,
  });
  it("positions bars proportionally and marks open ones", () => {
    const a = Date.parse("2026-09-21T00:00:00Z");
    const b = Date.parse("2026-09-21T10:00:00Z");
    const l = layoutGantt([mk("1", "2026-09-21T00:00:00Z", "2026-09-21T05:00:00Z"), mk("2", "2026-09-21T05:00:00Z", null)], a, b, b);
    expect(l.bars[0].leftPct).toBeCloseTo(0);
    expect(l.bars[0].widthPct).toBeCloseTo(50);
    expect(l.bars[1].leftPct).toBeCloseTo(50);
    expect(l.bars[1].open).toBe(true);
    expect(l.bars[1].widthPct).toBeCloseTo(50);
    expect(l.ticks).toHaveLength(6);
  });
  it("keeps very short processes visible", () => {
    const a = Date.parse("2026-09-21T00:00:00Z");
    const l = layoutGantt(
      [mk("1", "2026-09-21T00:00:00Z", "2026-09-21T00:00:01Z"), mk("2", "2026-09-21T10:00:00Z", "2026-09-21T11:00:00Z")],
      a,
      a + 86400_000,
      a + 86400_000,
    );
    expect(l.bars[0].widthPct).toBeGreaterThanOrEqual(0.4);
  });
});

describe("describeEvent", () => {
  const ev = (eventType: string, payload: EventDto["payload"]): EventDto => ({
    eventId: "e",
    eventType,
    timestampUtc: "2026-09-21T09:00:00Z",
    localOffsetMinutes: 330,
    deviceId: "PC",
    employeeCode: null,
    source: "s",
    payload,
  });
  it("describes foreground and background with durations", () => {
    expect(describeEvent(ev("APPLICATION_FOREGROUND", { processName: "excel.exe", windowTitle: "Q3.xlsx" }))).toEqual({
      title: "excel.exe in foreground",
      detail: "Q3.xlsx",
    });
    expect(describeEvent(ev("APPLICATION_BACKGROUND", { processName: "excel.exe", durationSeconds: 600, interactionSeconds: 300 })).detail).toBe(
      "foreground 10m, actively used 5m",
    );
  });
  it("falls back to the humanised event type", () => {
    expect(describeEvent(ev("SESSION_LOCK", {})).title).toBe("Session lock");
  });
});

describe("policy validation", () => {
  it("accepts the defaults", () => expect(validatePolicy(DEFAULT_POLICY)).toEqual([]));
  it("flags file monitoring without folders and oversize batches", () => {
    const issues = validatePolicy({ ...DEFAULT_POLICY, uploadBatchSize: 900, fileMonitoring: { enabled: true, paths: [], ignoreExtensions: [] } });
    expect(issues.map((i) => i.field)).toEqual(expect.arrayContaining(["uploadBatchSize", "fileMonitoring.paths"]));
  });
});
