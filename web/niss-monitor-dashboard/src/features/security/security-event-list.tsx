"use client";

import clsx from "clsx";
import Link from "next/link";
import { Pill, StatusPill } from "@/components/ui/primitives";
import { EmptyState } from "@/components/ui/states";
import { Time } from "@/components/ui/widgets";
import { humanize } from "@/lib/format";
import { severityTone } from "@/lib/status";
import { isTamperKind } from "@/features/security/tamper";
import type { SecurityEventDto } from "@/lib/types";

/** Compact list of security / agent-health events (overview, device diagnostics). */
export function SecurityEventList({ events }: { events: SecurityEventDto[] }) {
  if (events.length === 0) return <EmptyState title="No security events" hint="Agent health and authentication problems will be listed here." />;
  return (
    <ul className="divide-y divide-line">
      {events.map((e) => (
        <li
          key={e.id}
          className={clsx(
            "flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5",
            // an unacknowledged tamper event (stop/kill/removal) stands out from ordinary health noise
            isTamperKind(e.kind) && !e.resolvedAtUtc && "border-l-2 border-danger/60 bg-danger-soft",
          )}
        >
          <StatusPill value={e.severity} tone={severityTone(e.severity)} />
          <span className="text-[13px] font-medium">{humanize(e.kind)}</span>
          {e.deviceId && (
            <Link href={`/devices/${encodeURIComponent(e.deviceId)}`} className="text-[12.5px] text-brand hover:underline">
              {e.deviceId}
            </Link>
          )}
          <span className="min-w-0 basis-full truncate text-[12.5px] text-muted sm:basis-auto sm:flex-1" title={e.message}>{e.message}</span>
          <Time value={e.raisedAtUtc} className="text-[12px] text-faint" />
          {e.resolvedAtUtc && <Pill tone="ok">Resolved</Pill>}
        </li>
      ))}
    </ul>
  );
}
