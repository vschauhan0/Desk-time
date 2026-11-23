"use client";

import { ArrowLeft, LogIn, LogOut, Timer } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";
import { DurationBars, PRESENCE_COLORS } from "@/components/charts/charts";
import { Card, Notice, PageHeader, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Duration, KpiCard, RelativeTime, Time } from "@/components/ui/widgets";
import { APP_SERIES, AppUsageTable, totalsToBars, UsageLegendNote } from "@/features/shared/app-usage";
import { TimelineList } from "@/features/shared/timeline-list";
import { useApi } from "@/hooks/use-api";
import { rangeToQuery, resolveDateRange } from "@/lib/filters";
import { formatDuration, ratio, toDateInput } from "@/lib/format";
import { deviceStatusTone, presenceTone } from "@/lib/status";
import type { EmployeeDaySummary, TimelineItem } from "@/lib/types";

/** Segmented bar showing active / idle / locked shares of the logged-in day. */
export function PresenceBar({ active, idle, locked }: { active: number; idle: number; locked: number }) {
  const total = active + idle + locked;
  const seg = [
    { label: "Active", value: active, color: PRESENCE_COLORS.active },
    { label: "Idle", value: idle, color: PRESENCE_COLORS.idle },
    { label: "Locked", value: locked, color: PRESENCE_COLORS.locked },
  ];
  return (
    <div>
      <div className="flex h-3 overflow-hidden rounded-full bg-surface-3" role="img" aria-label={`Active ${formatDuration(active)}, idle ${formatDuration(idle)}, locked ${formatDuration(locked)}`}>
        {seg.map((s) => (
          <div key={s.label} style={{ width: `${ratio(s.value, total)}%`, background: s.color, boxShadow: "inset -2px 0 0 var(--surface)" }} title={`${s.label}: ${formatDuration(s.value)}`} />
        ))}
      </div>
      <ul className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-[12.5px]">
        {seg.map((s) => (
          <li key={s.label} className="flex items-center gap-1.5">
            <span className="inline-block size-2.5 rounded-sm" style={{ background: s.color }} aria-hidden />
            <span className="text-muted">{s.label}</span>
            <span className="font-medium tabular-nums">{formatDuration(s.value)}</span>
            <span className="text-faint tabular-nums">({Math.round(ratio(s.value, total))}%)</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function EmployeeDetail() {
  const params = useParams<{ id: string }>();
  const id = decodeURIComponent(params.id);
  const [date, setDate] = useState(() => toDateInput(new Date()));

  const range = useMemo(() => {
    const r = resolveDateRange("custom", { from: date, to: date });
    return { ...rangeToQuery(r) };
  }, [date]);
  const enc = encodeURIComponent(id);
  const summary = useApi<EmployeeDaySummary>(`/employees/${enc}/summary`, { date, tzOffsetMinutes: range.tzOffsetMinutes }, { refreshInterval: 60_000 });
  const timeline = useApi<TimelineItem[]>(`/employees/${enc}/timeline`, { from: range.from, to: range.to });
  const s = summary.data;

  return (
    <>
      <PageHeader
        title={s ? s.employee.fullName : "Employee"}
        subtitle={s ? `${s.employee.employeeCode}${s.employee.department ? ` - ${s.employee.department}` : ""}` : undefined}
        actions={
          <>
            <Link href="/employees" className="btn"><ArrowLeft size={14} aria-hidden /> Employees</Link>
            <label className="flex items-center gap-2 text-[13px] text-muted">
              Date
              <input className="field !w-40" type="date" value={date} max={toDateInput(new Date())} onChange={(e) => e.target.value && setDate(e.target.value)} aria-label="Day" />
            </label>
          </>
        }
      />
      <DataState loading={summary.loading} error={summary.error} onRetry={() => void summary.mutate()}>
        {s && (
          <div className="space-y-5">
            <div className="flex flex-wrap items-center gap-3 text-[13px]">
              {s.employee.status && <StatusPill value={s.employee.status} tone={deviceStatusTone(s.employee.status)} />}
              {s.employee.presence && <StatusPill value={s.employee.presence} tone={presenceTone(s.employee.presence)} />}
              <span className="text-muted">Last seen <RelativeTime value={s.employee.lastSeenUtc} /></span>
              {s.devices.length > 0 && (
                <span className="text-muted">
                  Devices:{" "}
                  {s.devices.map((d, i) => (
                    <span key={d}>
                      {i > 0 && ", "}
                      <Link className="text-brand hover:underline" href={`/devices/${encodeURIComponent(d)}`}>{d}</Link>
                    </span>
                  ))}
                </span>
              )}
            </div>

            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
              <KpiCard label="First login" value={<Time value={s.firstLoginUtc} className="text-lg" />} icon={LogIn} tone="ok" />
              <KpiCard label="Last logout" value={s.loggedInNow ? <span className="text-lg">Still logged in</span> : <Time value={s.lastLogoutUtc} className="text-lg" />} icon={LogOut} />
              <KpiCard label="Logged in" value={<Duration seconds={s.loggedInSeconds} />} hint={s.loggedInNow ? "Session in progress" : "Total for the day"} icon={Timer} tone="brand" />
              <KpiCard label="Active time" value={<Duration seconds={s.activeSeconds} />} hint={`Idle ${formatDuration(s.idleSeconds)} - Locked ${formatDuration(s.lockedSeconds)}`} />
            </div>

            <Card title="Active vs idle vs locked">
              <PresenceBar active={s.activeSeconds} idle={s.idleSeconds} locked={s.lockedSeconds} />
              <p className="mt-2 text-[11.5px] text-faint">Active = input within the idle threshold. Idle = no input beyond the threshold. Locked = workstation locked.</p>
            </Card>

            <Card title="Application usage" padded={false}>
              <div className="space-y-4 p-4">
                <UsageLegendNote />
                {s.applications.length > 0 && <DurationBars data={totalsToBars(s.applications)} series={APP_SERIES} label="Application usage" />}
              </div>
              {s.applications.length === 0 ? (
                <p className="px-4 pb-4 text-[13px] text-muted">No application activity on this day.</p>
              ) : (
                <AppUsageTable totals={s.applications} />
              )}
            </Card>

            <Card title="Timeline">
              <DataState loading={timeline.loading} error={timeline.error} onRetry={() => void timeline.mutate()}>
                <TimelineList items={timeline.data ?? []} />
              </DataState>
            </Card>
            {s.applications.length === 0 && s.firstLoginUtc === null && <Notice tone="info">No sessions were recorded for this employee on {date}.</Notice>}
          </div>
        )}
      </DataState>
    </>
  );
}
