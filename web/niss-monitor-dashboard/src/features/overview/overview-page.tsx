"use client";

import { Activity, AlertTriangle, CalendarClock, MonitorCheck, MonitorOff, Moon, Lock, Users, MonitorPlay } from "lucide-react";
import Link from "next/link";
import { useMemo } from "react";
import { DurationBars, PRESENCE_COLORS, StackedAreaByHour } from "@/components/charts/charts";
import { Card, PageHeader } from "@/components/ui/primitives";
import { DataState, EmptyState } from "@/components/ui/states";
import { KpiCard } from "@/components/ui/widgets";
import { SecurityEventList } from "@/features/security/security-event-list";
import { TamperAlertBanner } from "@/features/security/tamper-banner";
import { APP_SERIES, totalsToBars, UsageLegendNote } from "@/features/shared/app-usage";
import { useApi } from "@/hooks/use-api";
import { formatNumber, ratio } from "@/lib/format";
import type { OverviewDto } from "@/lib/types";

function VersionBars({ versions }: { versions: OverviewDto["agentVersions"] }) {
  const total = versions.reduce((n, v) => n + v.count, 0);
  if (versions.length === 0) return <EmptyState title="No agents reported a version yet" />;
  return (
    <ul className="space-y-2.5">
      {[...versions].sort((a, b) => b.count - a.count).map((v) => (
        <li key={v.version}>
          <div className="mb-1 flex justify-between text-[12.5px]">
            <span className="mono">{v.version}</span>
            <span className="tabular-nums text-muted">{v.count} ({Math.round(ratio(v.count, total))}%)</span>
          </div>
          <div className="h-2 overflow-hidden rounded-full bg-surface-3" role="presentation">
            <div className="h-full rounded-full" style={{ width: `${ratio(v.count, total)}%`, background: "var(--c1)" }} />
          </div>
        </li>
      ))}
    </ul>
  );
}

export function OverviewPage() {
  const { data, error, loading, mutate } = useApi<OverviewDto>("/overview", undefined, { refreshInterval: 30_000 });

  const hourly = useMemo(() => {
    const byHour = new Map((data?.activityByHour ?? []).map((h) => [h.hour, h]));
    return Array.from({ length: 24 }, (_, hour) => ({
      hour,
      active: byHour.get(hour)?.activeSeconds ?? 0,
      idle: byHour.get(hour)?.idleSeconds ?? 0,
    }));
  }, [data]);

  return (
    <>
      <PageHeader title="Overview" subtitle="Fleet status and today's activity at a glance." />
      <TamperAlertBanner />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} skeleton={<div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{Array.from({ length: 8 }, (_, i) => <div key={i} className="card skeleton h-24" />)}</div>}>
        {data && (
          <div className="space-y-5">
            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
              <KpiCard label="Devices online" value={data.online} hint={`${data.totalDevices} enrolled`} icon={MonitorCheck} tone="ok" />
              <KpiCard label="Warning" value={data.warning} hint="Heartbeat delayed" icon={MonitorPlay} tone={data.warning > 0 ? "warn" : "neutral"} />
              <KpiCard label="Offline" value={data.offline} hint={`${data.disabled} disabled`} icon={MonitorOff} tone={data.offline > 0 ? "danger" : "neutral"} />
              <KpiCard label="Open alerts" value={data.openAlerts} hint="Unresolved security events" icon={AlertTriangle} tone={data.openAlerts > 0 ? "danger" : "ok"} />
              <KpiCard label="Active now" value={data.activeNow} hint="Input within idle threshold" icon={Activity} tone="brand" />
              <KpiCard label="Idle now" value={data.idleNow} icon={Moon} tone="warn" />
              <KpiCard label="Locked now" value={data.lockedNow} icon={Lock} tone="info" />
              <KpiCard label="Employees" value={data.totalEmployees} hint={`${formatNumber(data.eventsToday)} events today`} icon={Users} />
            </div>

            <div className="grid gap-5 xl:grid-cols-3">
              <Card title="Activity by hour (today)" className="xl:col-span-2">
                <StackedAreaByHour
                  data={hourly}
                  series={[
                    { key: "active", label: "Active", color: PRESENCE_COLORS.active },
                    { key: "idle", label: "Idle", color: PRESENCE_COLORS.idle },
                  ]}
                />
                <p className="mt-1 text-[11.5px] text-faint">Summed across all devices. Idle = no input beyond the idle threshold.</p>
              </Card>
              <Card title="Agent versions">
                <VersionBars versions={data.agentVersions} />
              </Card>
            </div>

            <div className="grid gap-5 xl:grid-cols-2">
              <Card title="Top applications today" actions={<Link href="/applications" className="text-[12.5px] text-brand hover:underline">All activity</Link>}>
                {data.topApplicationsToday.length === 0 ? (
                  <EmptyState title="No application activity yet today" />
                ) : (
                  <div className="space-y-3">
                    <DurationBars data={totalsToBars(data.topApplicationsToday, 8)} series={APP_SERIES} label="Top applications today" />
                    <UsageLegendNote />
                  </div>
                )}
              </Card>
              <Card
                title="Recent security events"
                padded={false}
                actions={<Link href="/security" className="text-[12.5px] text-brand hover:underline">View all</Link>}
              >
                <SecurityEventList events={data.recentSecurityEvents} />
              </Card>
            </div>
            <p className="flex items-center gap-1.5 text-[11.5px] text-faint"><CalendarClock size={12} aria-hidden /> Refreshes every 30 s and instantly on device status changes.</p>
          </div>
        )}
      </DataState>
    </>
  );
}
