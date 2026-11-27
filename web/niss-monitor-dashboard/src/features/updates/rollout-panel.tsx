"use client";

import { Pause, Rocket, Undo2 } from "lucide-react";
import { Button, Card, Notice, Pill } from "@/components/ui/primitives";
import { Time } from "@/components/ui/widgets";
import { humanize } from "@/lib/format";
import type { Tone } from "@/lib/status";
import type { AgentRolloutDto, DeviceUpdateStateDto } from "@/lib/types";
import { deviceOutcome, fleetProgress, rolloutControls, rolloutProgress, type Progress } from "./updates";

const STATE_TONE: Record<string, Tone> = { Draft: "neutral", Canary: "info", RolledOut: "ok", Done: "ok", Paused: "warn", RolledBack: "danger" };
const OUTCOME_TONE: Record<string, Tone> = { updated: "ok", failed: "danger", pending: "warn" };

function ProgressBar({ label, p }: { label: string; p: Progress }) {
  const pct = (n: number) => (p.total > 0 ? (n / p.total) * 100 : 0);
  return (
    <div className="space-y-1.5">
      <div className="flex items-baseline justify-between text-[13px]">
        <span className="font-medium">{label}</span>
        <span className="text-muted">{p.total} device{p.total === 1 ? "" : "s"}</span>
      </div>
      <div className="flex h-2.5 overflow-hidden rounded-full bg-surface-2" role="img" aria-label={`${label}: ${p.updated} updated, ${p.failed} failed, ${p.pending} pending`}>
        <div className="bg-ok" style={{ width: `${pct(p.updated)}%` }} />
        <div className="bg-danger" style={{ width: `${pct(p.failed)}%` }} />
        <div className="bg-warn" style={{ width: `${pct(p.pending)}%` }} />
      </div>
      <div className="flex gap-4 text-[12px] tabular-nums">
        <span>Updated <b>{p.updated}</b></span>
        <span>Failed <b>{p.failed}</b></span>
        <span>Pending <b>{p.pending}</b></span>
      </div>
    </div>
  );
}

export interface RolloutPanelProps {
  rollout: AgentRolloutDto;
  devices: DeviceUpdateStateDto[];
  busy: boolean;
  onRollOut: () => void;
  onPause: () => void;
  onRollback: () => void;
}

/** One rollout: state, canary + fleet progress, lifecycle controls and the per-device status table. */
export function RolloutPanel({ rollout, devices, busy, onRollOut, onPause, onRollback }: RolloutPanelProps) {
  const controls = rolloutControls(rollout);
  const showGateHint = !rollout.gateOpen && (rollout.state === "Canary" || rollout.state === "Paused") && rollout.gateReason;
  return (
    <Card
      title={
        <span className="flex items-center gap-2">
          Version {rollout.version}
          <Pill tone={STATE_TONE[rollout.state] ?? "neutral"}>{humanize(rollout.state)}</Pill>
        </span>
      }
      actions={
        <div className="flex gap-2">
          <Button size="sm" icon={Pause} disabled={busy || controls.pauseDisabled} onClick={onPause}>Pause</Button>
          <Button size="sm" variant="danger" icon={Undo2} disabled={busy || controls.rollbackDisabled} onClick={onRollback}>Roll back</Button>
          <Button size="sm" variant="primary" icon={Rocket} disabled={busy || controls.rollOutDisabled} onClick={onRollOut} title={rollout.gateOpen ? undefined : "Available once the canary is healthy and has baked"}>
            Roll out to all
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        {showGateHint && <Notice tone="warn">Not ready to roll out to everyone: {rollout.gateReason}</Notice>}
        <ProgressBar label="Canary" p={rolloutProgress(rollout)} />
        <ProgressBar label="Fleet" p={fleetProgress(devices, rollout.version)} />
        {devices.length > 0 && (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr><th>Device</th><th>Status</th><th>Phase</th><th>Updated</th><th>Last error</th></tr>
              </thead>
              <tbody>
                {devices.map((d) => {
                  const o = deviceOutcome(d, rollout.version);
                  return (
                    <tr key={d.deviceId}>
                      <td className="font-medium">{d.deviceId}</td>
                      <td>{o ? <Pill tone={OUTCOME_TONE[o]}>{humanize(o)}</Pill> : "-"}</td>
                      <td>{humanize(d.phase)}</td>
                      <td><Time value={d.updatedAtUtc} /></td>
                      <td className="max-w-[320px] truncate text-muted" title={d.lastError ?? undefined}>{d.lastError ?? "-"}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Card>
  );
}
