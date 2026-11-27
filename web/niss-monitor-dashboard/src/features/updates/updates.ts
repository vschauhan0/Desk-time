import type { AgentRolloutDto, DeviceUpdateStateDto } from "@/lib/types";

export interface Progress {
  updated: number;
  failed: number;
  pending: number;
  total: number;
}

/** Canary progress straight from the server's counts. */
export function rolloutProgress(r: Pick<AgentRolloutDto, "canary" | "updated" | "failed" | "pending">): Progress {
  return { updated: r.updated, failed: r.failed, pending: r.pending, total: r.canary };
}

function same(a: string | null | undefined, b: string): boolean {
  return a != null && a.trim().toLowerCase() === b.trim().toLowerCase();
}

/** Outcome of one device for the rollout version: Succeeded = updated, an unresolved FailedVersion = failed, anything else is still pending. */
export function deviceOutcome(d: DeviceUpdateStateDto, version: string): "updated" | "failed" | "pending" | null {
  if (!same(d.assignedVersion, version)) return null;
  if (same(d.failedVersion, version)) return "failed";
  return d.phase === "Succeeded" ? "updated" : "pending";
}

/** Fleet progress from per-device states (only devices assigned this version count). */
export function fleetProgress(devices: DeviceUpdateStateDto[], version: string): Progress {
  const p: Progress = { updated: 0, failed: 0, pending: 0, total: 0 };
  for (const d of devices) {
    const o = deviceOutcome(d, version);
    if (!o) continue;
    p[o]++;
    p.total++;
  }
  return p;
}

export interface RolloutControls {
  rollOutDisabled: boolean;
  pauseDisabled: boolean;
  rollbackDisabled: boolean;
}

/** Which lifecycle buttons are usable. "Roll out to all" additionally needs the server's canary health gate to be open. */
export function rolloutControls(r: Pick<AgentRolloutDto, "state" | "gateOpen">): RolloutControls {
  return {
    rollOutDisabled: !(r.gateOpen && (r.state === "Canary" || r.state === "Paused")),
    pauseDisabled: !(r.state === "Canary" || r.state === "RolledOut"),
    rollbackDisabled: !(r.state === "Canary" || r.state === "RolledOut" || r.state === "Paused"),
  };
}
