"use client";

import clsx from "clsx";
import { ChevronDown, ChevronRight, Wifi, WifiOff } from "lucide-react";
import Link from "next/link";
import { memo, useState } from "react";
import { AppStateBadges, Pill, StatusPill } from "@/components/ui/primitives";
import { MiniBar, RelativeTime } from "@/components/ui/widgets";
import { deviceStatusTone, loadTone, presenceTone } from "@/lib/status";
import type { LiveDeviceCard } from "@/lib/types";

/** Presence as the card shows it: Active / Idle / Locked come from the card flags, otherwise the presence string. */
function cardPresence(card: LiveDeviceCard): string {
  if (card.isLocked) return "LOCKED";
  if (card.isIdle) return "IDLE";
  return card.presence;
}

function Metric({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <p className="text-[10.5px] font-semibold uppercase tracking-wider text-faint">{label}</p>
      <div className="mt-0.5 text-[13px]">{children}</div>
    </div>
  );
}

function DeviceCardImpl({ card, defaultExpanded = false }: { card: LiveDeviceCard; defaultExpanded?: boolean }) {
  const [open, setOpen] = useState(defaultExpanded);
  const presence = cardPresence(card);
  const apps = [...card.applications].sort((a, b) => Number(b.foreground) - Number(a.foreground) || Number(b.interaction) - Number(a.interaction) || a.processName.localeCompare(b.processName));

  return (
    <article className="card flex min-w-0 flex-col p-4" aria-label={`${card.hostname} live state`}>
      <header className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <Link href={`/devices/${encodeURIComponent(card.deviceId)}`} className="block truncate text-[14px] font-semibold hover:text-brand hover:underline">
            {card.hostname}
          </Link>
          <p className="truncate text-[12px] text-muted">
            {card.employeeName ?? "Unassigned"}
            {card.employeeCode ? ` (${card.employeeCode})` : ""}
            {card.department ? ` - ${card.department}` : ""}
          </p>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          <StatusPill value={card.status} tone={deviceStatusTone(card.status)} />
          <StatusPill value={presence} tone={presenceTone(presence)} />
        </div>
      </header>

      <div className="mt-3 rounded-lg bg-surface-2 px-3 py-2">
        <p className="text-[10.5px] font-semibold uppercase tracking-wider text-faint">Current application</p>
        <p className="truncate text-[13px] font-medium" title={card.currentApplication ?? undefined}>
          {card.currentApplication ?? "None in foreground"}
        </p>
        <p className="truncate text-[12px] text-muted" title={card.currentWindow ?? undefined}>
          {card.currentWindow ?? "—"}
        </p>
        <p className="mt-1 flex items-center gap-1.5 text-[11.5px]">
          <span className={clsx("inline-block size-1.5 rounded-full", card.isInteracting ? "bg-ok" : "bg-faint")} aria-hidden />
          <span className={card.isInteracting ? "text-ok" : "text-muted"}>{card.isInteracting ? "Interacting now" : "No recent input"}</span>
        </p>
      </div>

      <div className="mt-3 grid grid-cols-2 gap-x-4 gap-y-2.5">
        <Metric label="CPU"><MiniBar percent={card.cpuPercent} tone={loadTone(card.cpuPercent)} /></Metric>
        <Metric label="RAM"><MiniBar percent={card.memoryUsedPercent} tone={loadTone(card.memoryUsedPercent)} /></Metric>
        <Metric label="Network">
          {card.networkConnected === null ? (
            <span className="text-faint">{"—"}</span>
          ) : card.networkConnected ? (
            <span className="inline-flex items-center gap-1 text-ok"><Wifi size={13} aria-hidden /> Connected</span>
          ) : (
            <span className="inline-flex items-center gap-1 text-danger"><WifiOff size={13} aria-hidden /> Disconnected</span>
          )}
        </Metric>
        <Metric label="Last heartbeat"><RelativeTime value={card.lastHeartbeatUtc} /></Metric>
        <Metric label="Agent"><span className="mono">{card.agentVersion}</span></Metric>
        <Metric label="User">{card.userName ?? "—"}</Metric>
      </div>

      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="mt-3 flex items-center gap-1 self-start rounded text-[12.5px] font-medium text-brand hover:underline"
      >
        {open ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
        Applications ({apps.length})
      </button>

      {open && (
        <div className="mt-2 max-h-72 overflow-auto rounded-lg border border-line">
          {apps.length === 0 ? (
            <p className="p-3 text-[12.5px] text-muted">No window-owning applications are open.</p>
          ) : (
            <ul className="divide-y divide-line">
              {apps.map((a) => (
                <li key={`${a.processName}-${a.processId}`} className="flex flex-col gap-1 px-3 py-2">
                  <div className="flex items-baseline justify-between gap-2">
                    <span className="truncate font-medium">{a.processName}</span>
                    <span className="mono shrink-0 text-faint">PID {a.processId}</span>
                  </div>
                  {a.windowTitle && <p className="truncate text-[12px] text-muted" title={a.windowTitle}>{a.windowTitle}</p>}
                  <AppStateBadges running={a.running} foreground={a.foreground} interaction={a.interaction} />
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {card.status === "DISABLED" || card.status === "REVOKED" ? (
        <div className="mt-3"><Pill tone="neutral">Agent not reporting: {card.status.toLowerCase()}</Pill></div>
      ) : null}
    </article>
  );
}

export const DeviceCard = memo(DeviceCardImpl);
