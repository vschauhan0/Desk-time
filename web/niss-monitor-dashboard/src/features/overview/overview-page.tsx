"use client";

import { Activity, AlertTriangle, Flag, MonitorCheck, Play, ShieldAlert, Users, Zap } from "lucide-react";
import Link from "next/link";
import type { LucideIcon } from "lucide-react";
import { useMemo } from "react";
import { Donut, DurationBars, PRESENCE_COLORS, SERIES_COLORS, StackedAreaByHour, type DonutSlice } from "@/components/charts/charts";
import { Card, Pill } from "@/components/ui/primitives";
import { DataState, EmptyState } from "@/components/ui/states";
import { RelativeTime } from "@/components/ui/widgets";
import { SecurityEventList } from "@/features/security/security-event-list";
import { TamperAlertBanner } from "@/features/security/tamper-banner";
import { APP_SERIES, totalsByCategory, totalsToBars } from "@/features/shared/app-usage";
import { useApi } from "@/hooks/use-api";
import { formatNumber } from "@/lib/format";
import type { FlaggedCaptureDto, OverviewDto } from "@/lib/types";

/** Compact stat for the top strip: a label, a big number, and an icon. Links when a href is given. */
function Stat({ label, value, hint, icon: Icon, tone = "neutral", href }: { label: string; value: number | string; hint?: string; icon: LucideIcon; tone?: "neutral" | "ok" | "warn" | "danger" | "brand"; href?: string }) {
  const tint: Record<string, string> = {
    neutral: "text-muted",
    ok: "text-ok",
    warn: "text-warn",
    danger: "text-danger",
    brand: "text-brand",
  };
  const body = (
    <div className="card flex items-center gap-3 px-3.5 py-2.5">
      <span className={`flex size-8 shrink-0 items-center justify-center rounded-lg bg-surface-2 ${tint[tone]}`}>
        <Icon size={16} aria-hidden />
      </span>
      <div className="min-w-0">
        <p className="truncate text-[11px] font-medium uppercase tracking-wide text-faint">{label}</p>
        <p className="flex items-baseline gap-1.5 leading-tight">
          <span className="text-xl font-semibold tabular-nums">{value}</span>
          {hint && <span className="truncate text-[11px] text-muted">{hint}</span>}
        </p>
      </div>
    </div>
  );
  return href ? <Link href={href} className="block transition-colors hover:[&>div]:border-line-strong">{body}</Link> : body;
}

/** One recent flagged capture row. Clip rows link to the device's Screens tab where the replay lives. */
function FlaggedRow({ f }: { f: FlaggedCaptureDto }) {
  return (
    <Link href={`/devices/${encodeURIComponent(f.deviceId)}`} className="flex items-center gap-2.5 px-3 py-2 hover:bg-surface-2">
      <span className="flex size-6 shrink-0 items-center justify-center rounded-md bg-danger-soft text-danger">
        {f.clipId ? <Play size={12} aria-hidden /> : <Flag size={12} aria-hidden />}
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-[12.5px] font-medium">
          {f.flagTerm ?? "flagged"} <span className="font-normal text-muted">· {f.deviceId}</span>
        </p>
        <p className="truncate text-[11px] text-faint">{f.employeeName ?? "Unassigned"}</p>
      </div>
      <RelativeTime value={f.capturedAtUtc} className="shrink-0 text-[11px] text-faint" />
    </Link>
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

  const statusSlices: DonutSlice[] = data
    ? [
        { key: "online", label: "Online", value: data.online, color: "var(--ok)" },
        { key: "warning", label: "Warning", value: data.warning, color: "var(--warn)" },
        { key: "offline", label: "Offline", value: data.offline, color: "var(--danger)" },
        { key: "disabled", label: "Disabled", value: data.disabled, color: "var(--faint)" },
      ]
    : [];

  const categories = useMemo(() => (data ? totalsByCategory(data.topApplicationsToday).slice(0, 6) : []), [data]);
  const recentFlagged = data?.recentFlagged ?? [];

  return (
    <div className="viewport-fit flex flex-col gap-3">
      <div className="flex shrink-0 flex-wrap items-end justify-between gap-2">
        <div>
          <h1 className="text-xl font-semibold tracking-tight">Overview</h1>
          <p className="text-[12.5px] text-muted">Fleet status and today&apos;s activity at a glance.</p>
        </div>
      </div>
      <TamperAlertBanner />

      <DataState loading={loading} error={error} onRetry={() => void mutate()} skeleton={<div className="grid min-h-0 flex-1 gap-3 lg:grid-cols-12">{Array.from({ length: 6 }, (_, i) => <div key={i} className="card skeleton lg:col-span-4 xl:col-span-2" style={{ minHeight: 72 }} />)}</div>}>
        {data && (
          <div className="flex min-h-0 flex-1 flex-col gap-3">
            {/* Top strip: the six numbers not already in the fleet donut. */}
            <div className="grid shrink-0 grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-6">
              <Stat label="Devices" value={data.totalDevices} hint={`${data.online} online`} icon={MonitorCheck} tone="ok" href="/live" />
              <Stat label="Active now" value={data.activeNow} hint={`${data.idleNow} idle`} icon={Activity} tone="brand" href="/live" />
              <Stat label="Open alerts" value={data.openAlerts} icon={ShieldAlert} tone={data.openAlerts > 0 ? "danger" : "ok"} href="/security" />
              <Stat label="Flagged today" value={data.flaggedToday} icon={Flag} tone={data.flaggedToday > 0 ? "warn" : "neutral"} />
              <Stat label="Events today" value={formatNumber(data.eventsToday)} icon={Zap} tone="neutral" />
              <Stat label="Employees" value={data.totalEmployees} icon={Users} tone="neutral" href="/employees" />
            </div>

            {/* Main grid: two rows that each flex to fill the remaining viewport height. */}
            <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 lg:grid-cols-12 lg:grid-rows-2">
              {/* Row A */}
              <Card title="Fleet status" fill className="lg:col-span-3 lg:row-span-1">
                <div className="flex h-full flex-col justify-center gap-3">
                  <Donut slices={statusSlices} centerLabel="devices" height={150} />
                  <div className="border-t border-line pt-2.5">
                    <p className="mb-1.5 text-[11px] font-medium uppercase tracking-wide text-faint">Presence (reachable)</p>
                    <div className="flex items-center gap-3 text-[12px]">
                      <span className="flex items-center gap-1.5"><span className="size-2.5 rounded-sm" style={{ background: PRESENCE_COLORS.active }} /> {data.activeNow} active</span>
                      <span className="flex items-center gap-1.5"><span className="size-2.5 rounded-sm" style={{ background: PRESENCE_COLORS.idle }} /> {data.idleNow} idle</span>
                      <span className="flex items-center gap-1.5"><span className="size-2.5 rounded-sm" style={{ background: PRESENCE_COLORS.locked }} /> {data.lockedNow} locked</span>
                    </div>
                  </div>
                </div>
              </Card>

              <Card title="Activity today" fill className="lg:col-span-6 lg:row-span-1" actions={<span className="text-[11px] text-faint">active vs idle, all devices</span>}>
                <StackedAreaByHour
                  data={hourly}
                  height="fill"
                  series={[
                    { key: "active", label: "Active", color: PRESENCE_COLORS.active },
                    { key: "idle", label: "Idle", color: PRESENCE_COLORS.idle },
                  ]}
                />
              </Card>

              <Card
                title="Alerts & flagged activity"
                fill
                padded={false}
                className="lg:col-span-3 lg:row-span-1"
                actions={<Link href="/security" className="text-[11.5px] text-brand hover:underline">All</Link>}
              >
                <div className="flex h-full min-h-0 flex-col">
                  {data.openAlerts > 0 && (
                    <div className="flex shrink-0 items-center gap-2 border-b border-line bg-danger-soft px-3 py-2 text-[12px] text-danger">
                      <AlertTriangle size={13} aria-hidden /> {data.openAlerts} open security alert{data.openAlerts === 1 ? "" : "s"}
                    </div>
                  )}
                  <div className="min-h-0 flex-1 overflow-auto">
                    {recentFlagged.length > 0 ? (
                      <>
                        <p className="px-3 pt-2 text-[10.5px] font-semibold uppercase tracking-wide text-faint">Recent flagged</p>
                        <div className="divide-y divide-line">
                          {recentFlagged.map((f) => <FlaggedRow key={f.id} f={f} />)}
                        </div>
                      </>
                    ) : data.recentSecurityEvents.length > 0 ? (
                      <SecurityEventList events={data.recentSecurityEvents.slice(0, 5)} />
                    ) : (
                      <div className="grid h-full place-items-center p-4 text-center text-[12px] text-muted">
                        <span><Flag size={18} className="mx-auto mb-1 text-faint" aria-hidden />Nothing flagged. Quiet fleet.</span>
                      </div>
                    )}
                  </div>
                </div>
              </Card>

              {/* Row B */}
              <Card title="Top applications today" fill padded={false} className="lg:col-span-5 lg:row-span-1" actions={<Link href="/applications" className="text-[11.5px] text-brand hover:underline">All activity</Link>}>
                {data.topApplicationsToday.length === 0 ? (
                  <EmptyState title="No application activity yet today" />
                ) : (
                  <div className="h-full overflow-auto p-3">
                    <DurationBars data={totalsToBars(data.topApplicationsToday, 7)} series={APP_SERIES} label="Top applications today" rowHeight={26} />
                  </div>
                )}
              </Card>

              <Card title="By category" fill padded={false} className="lg:col-span-4 lg:row-span-1">
                {categories.length === 0 ? (
                  <EmptyState title="No category data yet" />
                ) : (
                  <div className="h-full overflow-auto p-3">
                    <DurationBars data={categories} series={APP_SERIES} stacked label="Usage by category" rowHeight={26} />
                  </div>
                )}
              </Card>

              <Card title="Agent versions" fill padded={false} className="lg:col-span-3 lg:row-span-1">
                <div className="h-full overflow-auto p-3">
                  <VersionBars versions={data.agentVersions} />
                </div>
              </Card>
            </div>
          </div>
        )}
      </DataState>
    </div>
  );
}

function VersionBars({ versions }: { versions: OverviewDto["agentVersions"] }) {
  const total = versions.reduce((n, v) => n + v.count, 0);
  if (versions.length === 0) return <EmptyState title="No agents reported a version yet" />;
  const sorted = [...versions].sort((a, b) => b.count - a.count);
  return (
    <ul className="space-y-2">
      {sorted.map((v, i) => {
        const pct = total === 0 ? 0 : (v.count / total) * 100;
        return (
          <li key={v.version}>
            <div className="mb-1 flex items-center justify-between gap-2 text-[12px]">
              <span className="mono truncate">{v.version}</span>
              {i === 0 && sorted.length > 1 && <Pill tone="ok" icon={false}>latest in use</Pill>}
              <span className="ml-auto shrink-0 tabular-nums text-muted">{v.count} ({Math.round(pct)}%)</span>
            </div>
            <div className="h-1.5 overflow-hidden rounded-full bg-surface-3" role="presentation">
              <div className="h-full rounded-full" style={{ width: `${pct}%`, background: SERIES_COLORS[i % SERIES_COLORS.length] }} />
            </div>
          </li>
        );
      })}
    </ul>
  );
}
