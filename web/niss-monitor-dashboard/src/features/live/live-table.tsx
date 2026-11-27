"use client";

import clsx from "clsx";
import { ArrowDown, ArrowUp, Wifi, WifiOff } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { memo, useMemo, useState } from "react";
import { StatusPill } from "@/components/ui/primitives";
import { MiniBar, RelativeTime } from "@/components/ui/widgets";
import { deviceStatusTone, loadTone, presenceTone } from "@/lib/status";
import type { LiveDeviceCard } from "@/lib/types";

type SortKey = "host" | "employee" | "status" | "presence" | "application" | "cpu" | "ram" | "heartbeat";

/** Presence as the row shows it: Locked/Idle flags win over the raw presence string. */
function rowPresence(c: LiveDeviceCard): string {
  if (c.isLocked) return "LOCKED";
  if (c.isIdle) return "IDLE";
  return c.presence;
}

const STATUS_RANK: Record<string, number> = { WARNING: 0, OFFLINE: 1, SERVICE_STOPPED: 1, ONLINE: 2, DISABLED: 3, REVOKED: 3, PENDING: 4 };
const PRESENCE_RANK: Record<string, number> = { ACTIVE: 0, IDLE: 1, LOCKED: 2 };

function compare(a: LiveDeviceCard, b: LiveDeviceCard, key: SortKey): number {
  switch (key) {
    case "host": return a.hostname.localeCompare(b.hostname);
    case "employee": return (a.employeeName ?? "~").localeCompare(b.employeeName ?? "~");
    case "status": return (STATUS_RANK[a.status] ?? 9) - (STATUS_RANK[b.status] ?? 9);
    case "presence": return (PRESENCE_RANK[rowPresence(a)] ?? 9) - (PRESENCE_RANK[rowPresence(b)] ?? 9);
    case "application": return (a.currentApplication ?? "~").localeCompare(b.currentApplication ?? "~");
    case "cpu": return (a.cpuPercent ?? -1) - (b.cpuPercent ?? -1);
    case "ram": return (a.memoryUsedPercent ?? -1) - (b.memoryUsedPercent ?? -1);
    case "heartbeat": return new Date(a.lastHeartbeatUtc ?? 0).getTime() - new Date(b.lastHeartbeatUtc ?? 0).getTime();
  }
}

function SortHead({ label, k, sort, dir, onSort, num }: { label: string; k: SortKey; sort: SortKey; dir: 1 | -1; onSort: (k: SortKey) => void; num?: boolean }) {
  const active = sort === k;
  return (
    <th data-sortable="true" className={num ? "num" : undefined} aria-sort={active ? (dir === 1 ? "ascending" : "descending") : "none"} onClick={() => onSort(k)}>
      <span className={clsx("inline-flex items-center gap-1", num && "flex-row-reverse")}>
        {label}
        {active && (dir === 1 ? <ArrowUp size={11} aria-hidden /> : <ArrowDown size={11} aria-hidden />)}
      </span>
    </th>
  );
}

function LiveRow({ card }: { card: LiveDeviceCard }) {
  const router = useRouter();
  const presence = rowPresence(card);
  const href = `/devices/${encodeURIComponent(card.deviceId)}`;
  return (
    <tr data-clickable="true" onClick={() => router.push(href)}>
      <td>
        <Link href={href} className="font-medium hover:text-brand hover:underline" onClick={(e) => e.stopPropagation()}>{card.hostname}</Link>
        <p className="truncate text-[11px] text-faint">{card.department ?? "—"}</p>
      </td>
      <td className="max-w-[150px]">
        <p className="truncate">{card.employeeName ?? "Unassigned"}</p>
        <p className="truncate text-[11px] text-faint">{card.userName ?? card.employeeCode ?? "—"}</p>
      </td>
      <td><StatusPill value={card.status} tone={deviceStatusTone(card.status)} /></td>
      <td>
        <span className="inline-flex items-center gap-1.5">
          <StatusPill value={presence} tone={presenceTone(presence)} />
          {card.isInteracting && <span className="size-1.5 rounded-full bg-ok" title="Interacting now" aria-label="Interacting now" />}
        </span>
      </td>
      <td className="max-w-[240px]">
        <p className="truncate font-medium" title={card.currentApplication ?? undefined}>{card.currentApplication ?? "—"}</p>
        {card.currentWindow && <p className="truncate text-[11px] text-faint" title={card.currentWindow}>{card.currentWindow}</p>}
      </td>
      <td className="num"><MiniBar percent={card.cpuPercent} tone={loadTone(card.cpuPercent)} /></td>
      <td className="num"><MiniBar percent={card.memoryUsedPercent} tone={loadTone(card.memoryUsedPercent)} /></td>
      <td className="num">
        {card.networkConnected === null ? (
          <span className="text-faint">—</span>
        ) : card.networkConnected ? (
          <Wifi size={14} className="ml-auto text-ok" aria-label="Connected" />
        ) : (
          <WifiOff size={14} className="ml-auto text-danger" aria-label="Disconnected" />
        )}
      </td>
      <td className="num"><RelativeTime value={card.lastHeartbeatUtc} /></td>
      <td className="num mono text-[11.5px] text-muted">{card.agentVersion}</td>
    </tr>
  );
}

const Row = memo(LiveRow);

/** Dense one-row-per-device table for scanning a large fleet. Click a row to open the device; headers sort. */
export function LiveTable({ cards }: { cards: LiveDeviceCard[] }) {
  const [sort, setSort] = useState<SortKey>("status");
  const [dir, setDir] = useState<1 | -1>(1);

  const onSort = (k: SortKey) => {
    if (k === sort) setDir((d) => (d === 1 ? -1 : 1));
    else {
      setSort(k);
      setDir(1);
    }
  };

  const rows = useMemo(() => {
    const sorted = [...cards].sort((a, b) => compare(a, b, sort) * dir);
    // Stable secondary sort by host so equal keys keep a predictable order.
    return sorted;
  }, [cards, sort, dir]);

  const head = { sort, dir, onSort };
  return (
    <div className="table-wrap h-full min-h-0 max-h-none">
      <table className="data-table">
        <thead>
          <tr>
            <SortHead label="Device" k="host" {...head} />
            <SortHead label="User" k="employee" {...head} />
            <SortHead label="Status" k="status" {...head} />
            <SortHead label="Presence" k="presence" {...head} />
            <SortHead label="Current application" k="application" {...head} />
            <SortHead label="CPU" k="cpu" num {...head} />
            <SortHead label="RAM" k="ram" num {...head} />
            <th className="num">Net</th>
            <SortHead label="Heartbeat" k="heartbeat" num {...head} />
            <th className="num">Agent</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((c) => <Row key={c.deviceId} card={c} />)}
        </tbody>
      </table>
    </div>
  );
}
