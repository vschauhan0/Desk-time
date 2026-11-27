"use client";

import Link from "next/link";
import { Notice } from "@/components/ui/primitives";
import { useApi } from "@/hooks/use-api";
import { humanize } from "@/lib/format";
import { type Paged, type SecurityEventDto } from "@/lib/types";
import { unresolvedTamper } from "./tamper";

/**
 * Loud, persistent banner for unacknowledged tamper alerts (agent stopped, killed or removed). Dashboard-only
 * delivery: driven by the authoritative unresolved-events query, which the SignalR layer revalidates within ~1.5s of
 * a new alert - so it pops without a reload and stays visible until the event is acknowledged on the Security page.
 */
export function TamperAlertBanner() {
  const { data } = useApi<Paged<SecurityEventDto>>(
    "/security-events",
    { resolved: "false", pageSize: 100 },
    { refreshInterval: 30_000 },
  );
  const alerts = unresolvedTamper(data?.items ?? []);
  if (alerts.length === 0) return null;

  const latest = alerts[0];
  const where = latest.deviceId ? ` on ${latest.deviceId}` : "";
  return (
    <div className="mb-4">
      <Notice tone="danger">
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <strong>
            {alerts.length} unacknowledged tamper alert{alerts.length === 1 ? "" : "s"}
          </strong>
          <span className="text-muted">
            — latest: {humanize(latest.kind)}
            {where}. {latest.message}
          </span>
          <Link href="/security" className="font-medium text-brand hover:underline">
            Review &amp; acknowledge →
          </Link>
        </span>
      </Notice>
    </div>
  );
}
