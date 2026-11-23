import { describe, expect, it } from "vitest";
import { cleanFilters, filterLiveCards, filtersToQuery, matchesSearch, rangeToQuery, resolveDateRange } from "../filters";

describe("resolveDateRange", () => {
  const now = new Date(2026, 8, 21, 15, 30, 0); // local time

  it("today = local midnight to next local midnight", () => {
    const r = resolveDateRange("today", null, now);
    expect(r.from).toEqual(new Date(2026, 8, 21));
    expect(r.to).toEqual(new Date(2026, 8, 22));
  });
  it("yesterday", () => {
    const r = resolveDateRange("yesterday", null, now);
    expect(r.from).toEqual(new Date(2026, 8, 20));
    expect(r.to).toEqual(new Date(2026, 8, 21));
  });
  it("last 7 days covers today and the 6 days before", () => {
    const r = resolveDateRange("last7", null, now);
    expect(r.from).toEqual(new Date(2026, 8, 15));
    expect(r.to).toEqual(new Date(2026, 8, 22));
  });
  it("custom is inclusive of the end day and tolerates swapped bounds", () => {
    const r = resolveDateRange("custom", { from: "2026-09-10", to: "2026-09-12" }, now);
    expect(r.from).toEqual(new Date(2026, 8, 10));
    expect(r.to).toEqual(new Date(2026, 8, 13));
    const swapped = resolveDateRange("custom", { from: "2026-09-12", to: "2026-09-10" }, now);
    expect(swapped).toEqual(r);
  });
  it("invalid custom values fall back to today", () => {
    const r = resolveDateRange("custom", { from: "", to: "x" }, now);
    expect(r.from).toEqual(new Date(2026, 8, 21));
  });
  it("crosses month boundaries", () => {
    const r = resolveDateRange("last7", null, new Date(2026, 9, 3));
    expect(r.from).toEqual(new Date(2026, 8, 27));
  });
});

describe("rangeToQuery", () => {
  it("emits ISO instants and the viewer's offset (minutes east of UTC)", () => {
    const from = new Date(2026, 8, 21);
    const q = rangeToQuery({ from, to: new Date(2026, 8, 22) });
    expect(q.from).toBe(from.toISOString());
    expect(q.tzOffsetMinutes).toBe(-from.getTimezoneOffset() || 0);
  });
});

describe("filters", () => {
  it("cleanFilters drops blanks and trims", () => {
    expect(cleanFilters({ employee: " E1 ", device: "", search: "   " })).toEqual({ employee: "E1" });
  });
  it("filtersToQuery only forwards allowed keys and applies renames", () => {
    const q = filtersToQuery({ process: "chrome", status: "RUNNING", employee: "E1", department: "IT" }, ["process", "status", "employee"], { process: "search" });
    expect(q).toEqual({ search: "chrome", status: "RUNNING", employee: "E1" });
  });
  it("matchesSearch is case-insensitive over several fields", () => {
    expect(matchesSearch("PC-0", "pc-024", null)).toBe(true);
    expect(matchesSearch("zzz", "pc-024", null)).toBe(false);
    expect(matchesSearch("", "anything")).toBe(true);
  });
  it("filterLiveCards combines status, department and search", () => {
    const base = { employeeName: null, currentApplication: null };
    const cards = [
      { ...base, deviceId: "PC-1", hostname: "ALPHA", status: "ONLINE", department: "IT", employeeCode: "E1" },
      { ...base, deviceId: "PC-2", hostname: "BETA", status: "OFFLINE", department: "IT", employeeCode: "E2" },
      { ...base, deviceId: "PC-3", hostname: "GAMMA", status: "ONLINE", department: "HR", employeeCode: "E3" },
    ];
    expect(filterLiveCards(cards, { status: "ONLINE" }).map((c) => c.deviceId)).toEqual(["PC-1", "PC-3"]);
    expect(filterLiveCards(cards, { status: "ONLINE", department: "IT" }).map((c) => c.deviceId)).toEqual(["PC-1"]);
    expect(filterLiveCards(cards, { search: "beta" }).map((c) => c.deviceId)).toEqual(["PC-2"]);
    expect(filterLiveCards(cards, { employee: "E3" }).map((c) => c.deviceId)).toEqual(["PC-3"]);
  });
});
