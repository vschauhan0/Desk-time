import { describe, expect, it } from "vitest";
import type { SecurityEventDto } from "@/lib/types";
import { isTamperKind, unresolvedTamper } from "../tamper";

const ev = (over: Partial<SecurityEventDto> = {}): SecurityEventDto => ({
  id: "x",
  deviceId: "PC-1",
  kind: "TAMPER_SUSPECTED",
  severity: "CRITICAL",
  message: "stopped while CONTOSO\\jdoe was signed in",
  raisedAtUtc: "2026-11-26T12:00:00Z",
  resolvedAtUtc: null,
  ...over,
});

describe("tamper alert classification", () => {
  it("treats a deliberate stop and an unclean shutdown as tamper", () => {
    expect(isTamperKind("TAMPER_SUSPECTED")).toBe(true);
    expect(isTamperKind("UNEXPECTED_SHUTDOWN")).toBe(true);
  });

  it("does not treat ordinary health events as tamper", () => {
    expect(isTamperKind("HEARTBEAT_DELAYED")).toBe(false);
    expect(isTamperKind("DEVICE_OFFLINE")).toBe(false);
    expect(isTamperKind("SERVICE_STOPPED")).toBe(false); // a plain stop report is not, by itself, a tamper alert
  });

  it("returns only unresolved tamper events, preserving (newest-first) order", () => {
    const events = [
      ev({ id: "a", kind: "TAMPER_SUSPECTED", resolvedAtUtc: null }),
      ev({ id: "b", kind: "TAMPER_SUSPECTED", resolvedAtUtc: "2026-11-26T13:00:00Z" }), // resolved -> excluded
      ev({ id: "c", kind: "DEVICE_OFFLINE", resolvedAtUtc: null }), // not tamper -> excluded
      ev({ id: "d", kind: "UNEXPECTED_SHUTDOWN", resolvedAtUtc: null }),
    ];
    expect(unresolvedTamper(events).map((e) => e.id)).toEqual(["a", "d"]);
  });

  it("is empty when nothing is wrong", () => {
    expect(unresolvedTamper([])).toEqual([]);
  });
});
