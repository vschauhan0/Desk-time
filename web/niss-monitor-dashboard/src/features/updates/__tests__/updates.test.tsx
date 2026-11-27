import { act } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AgentRolloutDto, DeviceUpdateStateDto } from "@/lib/types";
import { RolloutPanel } from "../rollout-panel";
import { fleetProgress, rolloutProgress } from "../updates";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const rollout = (over: Partial<AgentRolloutDto> = {}): AgentRolloutDto => ({
  id: "r1",
  version: "1.5.0.0",
  state: "Canary",
  createdAtUtc: "2026-11-27T10:00:00Z",
  canary: 5,
  updated: 2,
  failed: 1,
  pending: 2,
  gateOpen: false,
  gateReason: "the bake window runs until 2026-11-28 10:00Z.",
  ...over,
});

const dev = (over: Partial<DeviceUpdateStateDto>): DeviceUpdateStateDto => ({
  deviceId: "PC-1",
  assignedVersion: "1.5.0.0",
  phase: "Succeeded",
  lastError: null,
  failedVersion: null,
  updatedAtUtc: "2026-11-27T11:00:00Z",
  ...over,
});

let host: HTMLDivElement | null = null;
function mount(r: AgentRolloutDto, devices: DeviceUpdateStateDto[] = []) {
  host = document.createElement("div");
  document.body.appendChild(host);
  const root = createRoot(host);
  act(() => root.render(<RolloutPanel rollout={r} devices={devices} busy={false} onRollOut={vi.fn()} onPause={vi.fn()} onRollback={vi.fn()} />));
  return host;
}
afterEach(() => {
  host?.remove();
  host = null;
});

const rollOutButton = (h: HTMLElement) => [...h.querySelectorAll("button")].find((b) => /roll out to all/i.test(b.textContent ?? ""))!;

describe("roll-out gate", () => {
  it("the roll-out button is disabled until canary health passes", () => {
    const h = mount(rollout({ gateOpen: false }));
    expect(rollOutButton(h).disabled).toBe(true);
  });

  it("the roll-out button is enabled once the gate is open", () => {
    const h = mount(rollout({ gateOpen: true, gateReason: null }));
    expect(rollOutButton(h).disabled).toBe(false);
  });

  it("stays disabled when the gate is open but the rollout is no longer in a promotable state", () => {
    const h = mount(rollout({ gateOpen: true, state: "RolledBack" }));
    expect(rollOutButton(h).disabled).toBe(true);
  });
});

describe("progress", () => {
  it("splits canary progress into updated / failed / pending", () => {
    expect(rolloutProgress(rollout())).toEqual({ updated: 2, failed: 1, pending: 2, total: 5 });
  });

  it("splits fleet progress from per-device states for the rollout version", () => {
    const devices = [
      dev({ deviceId: "a" }),
      dev({ deviceId: "b" }),
      dev({ deviceId: "c", phase: "RolledBack", failedVersion: "1.5.0.0", lastError: "boom" }),
      dev({ deviceId: "d", phase: "Downloading" }),
      dev({ deviceId: "e", phase: "Assigned" }),
      dev({ deviceId: "f", phase: "Succeeded", assignedVersion: "1.4.0.0" }), // other version: ignored
    ];
    expect(fleetProgress(devices, "1.5.0.0")).toEqual({ updated: 2, failed: 1, pending: 2, total: 5 });
  });

  it("renders the three counts", () => {
    const h = mount(rollout());
    const text = h.textContent ?? "";
    expect(text).toMatch(/Updated\s*2/);
    expect(text).toMatch(/Failed\s*1/);
    expect(text).toMatch(/Pending\s*2/);
  });
});
