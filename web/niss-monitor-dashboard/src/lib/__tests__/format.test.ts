import { describe, expect, it } from "vitest";
import {
  compareVersions,
  formatCell,
  formatDuration,
  formatMegabytes,
  formatPercent,
  formatRelative,
  formatUtc,
  humanize,
  parseDateInput,
  ratio,
  toDateInput,
} from "../format";

describe("formatDuration", () => {
  it("formats hours and minutes like '2h 18m'", () => {
    expect(formatDuration(2 * 3600 + 18 * 60)).toBe("2h 18m");
    expect(formatDuration(2 * 3600 + 18 * 60 + 40)).toBe("2h 18m");
  });
  it("drops zero minutes", () => expect(formatDuration(3 * 3600)).toBe("3h"));
  it("formats minutes and seconds", () => {
    expect(formatDuration(12 * 60 + 5)).toBe("12m 5s");
    expect(formatDuration(12 * 60)).toBe("12m");
  });
  it("formats seconds and zero", () => {
    expect(formatDuration(45)).toBe("45s");
    expect(formatDuration(0)).toBe("0s");
    expect(formatDuration(-5)).toBe("0s");
  });
  it("formats days", () => {
    expect(formatDuration(86400 + 3 * 3600 + 120)).toBe("1d 3h");
    expect(formatDuration(2 * 86400)).toBe("2d");
  });
  it("rounds to the nearest second", () => expect(formatDuration(59.6)).toBe("1m"));
  it("renders null / NaN as a dash", () => {
    expect(formatDuration(null)).toBe("—");
    expect(formatDuration(undefined)).toBe("—");
    expect(formatDuration(Number.NaN)).toBe("—");
  });
});

describe("formatRelative", () => {
  const now = Date.parse("2026-09-21T10:00:00Z");
  it("ticks in seconds, minutes, hours, days", () => {
    expect(formatRelative("2026-09-21T09:59:52Z", now)).toBe("8 seconds ago");
    expect(formatRelative("2026-09-21T09:59:59Z", now)).toBe("1 second ago");
    expect(formatRelative("2026-09-21T09:57:00Z", now)).toBe("3 minutes ago");
    expect(formatRelative("2026-09-21T08:00:00Z", now)).toBe("2 hours ago");
    expect(formatRelative("2026-09-19T10:00:00Z", now)).toBe("2 days ago");
  });
  it("handles missing and invalid values", () => {
    expect(formatRelative(null, now)).toBe("never");
    expect(formatRelative("nonsense", now)).toBe("never");
  });
  it("tolerates small clock skew", () => expect(formatRelative("2026-09-21T10:00:02Z", now)).toBe("0 seconds ago"));
});

describe("misc formatters", () => {
  it("formatUtc", () => expect(formatUtc("2026-09-21T09:35:20.412Z")).toBe("2026-09-21 09:35:20 UTC"));
  it("formatPercent", () => {
    expect(formatPercent(4.26)).toBe("4.3%");
    expect(formatPercent(57.4)).toBe("57%");
    expect(formatPercent(null)).toBe("—");
  });
  it("formatMegabytes", () => {
    expect(formatMegabytes(512)).toBe("512 MB");
    expect(formatMegabytes(3276.8)).toBe("3.2 GB");
  });
  it("humanize", () => {
    expect(humanize("SERVICE_STOPPED")).toBe("Service stopped");
    expect(humanize(null)).toBe("—");
  });
  it("ratio clamps and guards zero", () => {
    expect(ratio(1, 4)).toBe(25);
    expect(ratio(5, 0)).toBe(0);
    expect(ratio(9, 3)).toBe(100);
  });
  it("date input round trip", () => {
    const d = parseDateInput("2026-09-21");
    expect(d).not.toBeNull();
    expect(toDateInput(d as Date)).toBe("2026-09-21");
    expect(parseDateInput("21/09/2026")).toBeNull();
  });
  it("compareVersions", () => {
    expect(compareVersions("1.10.0", "1.9.3")).toBe(1);
    expect(compareVersions("1.2", "1.2.0")).toBe(0);
    expect(compareVersions("0.9.9", "1.0.0")).toBe(-1);
  });
  it("formatCell honours column type", () => {
    expect(formatCell(3600, "duration")).toBe("1h");
    expect(formatCell(null)).toBe("—");
    expect(formatCell(true)).toBe("Yes");
    expect(formatCell("abc", "string")).toBe("abc");
  });
});
