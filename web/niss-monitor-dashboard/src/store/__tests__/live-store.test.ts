import { beforeEach, describe, expect, it } from "vitest";
import type { EventDto, LiveDeviceCard } from "@/lib/types";
import { applyStatusChange, prependCapped, useLiveStore } from "../live-store";

const card = (deviceId: string, over: Partial<LiveDeviceCard> = {}): LiveDeviceCard => ({
  deviceId,
  hostname: deviceId,
  employeeCode: null,
  employeeName: null,
  department: null,
  status: "ONLINE",
  presence: "ACTIVE",
  isIdle: false,
  isLocked: false,
  isInteracting: true,
  currentApplication: null,
  currentWindow: null,
  applications: [],
  cpuPercent: 5,
  memoryUsedPercent: 40,
  networkConnected: true,
  lastHeartbeatUtc: null,
  agentVersion: "1.0.0",
  userName: null,
  ...over,
});

const event = (id: string): EventDto => ({
  eventId: id,
  eventType: "SESSION_LOCK",
  timestampUtc: "",
  localOffsetMinutes: 0,
  deviceId: "a",
  employeeCode: null,
  source: "",
  payload: {},
});

beforeEach(() => useLiveStore.getState().reset());

describe("applyStatusChange", () => {
  it("patches only the affected card and returns the same map when nothing changes", () => {
    const cards = { a: card("a"), b: card("b") };
    const next = applyStatusChange(cards, { deviceId: "a", status: "OFFLINE", previousStatus: "ONLINE", reason: null, atUtc: "" });
    expect(next.a.status).toBe("OFFLINE");
    expect(next.b).toBe(cards.b);
    expect(applyStatusChange(next, { deviceId: "a", status: "OFFLINE", previousStatus: null, reason: null, atUtc: "" })).toBe(next);
    expect(applyStatusChange(cards, { deviceId: "zzz", status: "OFFLINE", previousStatus: null, reason: null, atUtc: "" })).toBe(cards);
  });
});

describe("prependCapped", () => {
  it("puts the newest first, de-duplicates by key and caps length", () => {
    expect(prependCapped(["a", "b", "c"], "b", (x) => x, 3)).toEqual(["b", "a", "c"]);
    expect(prependCapped(["a", "b", "c"], "d", (x) => x, 3)).toEqual(["d", "a", "b"]);
  });
});

describe("live store", () => {
  it("snapshot then SignalR patches", () => {
    const s = useLiveStore.getState();
    s.setSnapshot([card("a"), card("b")]);
    expect(useLiveStore.getState().loaded).toBe(true);
    s.upsertCard(card("a", { currentApplication: "excel.exe" }));
    s.applyStatusChange({ deviceId: "b", status: "WARNING", previousStatus: "ONLINE", reason: "late", atUtc: "" });
    const { cards } = useLiveStore.getState();
    expect(cards.a.currentApplication).toBe("excel.exe");
    expect(cards.b.status).toBe("WARNING");
  });
  it("keeps a capped, de-duplicated event feed", () => {
    const s = useLiveStore.getState();
    for (let i = 0; i < 350; i++) s.pushEvent(event(`e${i}`));
    s.pushEvent(event("e349"));
    const { feed } = useLiveStore.getState();
    expect(feed).toHaveLength(300);
    expect(feed[0].eventId).toBe("e349");
    expect(new Set(feed.map((e) => e.eventId)).size).toBe(300);
  });
  it("bumps resync and tracks hub state", () => {
    useLiveStore.getState().bumpResync();
    useLiveStore.getState().setHubState("reconnecting");
    expect(useLiveStore.getState().resync).toBe(1);
    expect(useLiveStore.getState().hubState).toBe("reconnecting");
  });
});
