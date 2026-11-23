"use client";

import { useMemo } from "react";
import { APP_CONCEPT_COLORS, DurationBars, type SeriesDef } from "@/components/charts/charts";
import { Card, Notice } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Duration, Pagination, Time } from "@/components/ui/widgets";
import { useApi } from "@/hooks/use-api";
import { usePage } from "@/hooks/use-page";
import type { Query } from "@/lib/api";
import { formatNumber } from "@/lib/format";
import type { AppUsageTotal, ApplicationActivityResponse } from "@/lib/types";

export const APP_SERIES: SeriesDef[] = [
  { key: "running", label: "Open / running", color: APP_CONCEPT_COLORS.running },
  { key: "foreground", label: "In foreground", color: APP_CONCEPT_COLORS.foreground },
  { key: "interaction", label: "Actively used", color: APP_CONCEPT_COLORS.interaction },
];

export function totalsToBars(totals: AppUsageTotal[], limit = 12): Record<string, string | number>[] {
  return [...totals]
    .sort((a, b) => b.foregroundSeconds + b.interactionSeconds - (a.foregroundSeconds + a.interactionSeconds))
    .slice(0, limit)
    .map((t) => ({ name: t.processName, running: t.runningSeconds, foreground: t.foregroundSeconds, interaction: t.interactionSeconds }));
}

export function totalsByCategory(totals: AppUsageTotal[]): Record<string, string | number>[] {
  const map = new Map<string, { running: number; foreground: number; interaction: number }>();
  for (const t of totals) {
    const acc = map.get(t.category) ?? { running: 0, foreground: 0, interaction: 0 };
    acc.running += t.runningSeconds;
    acc.foreground += t.foregroundSeconds;
    acc.interaction += t.interactionSeconds;
    map.set(t.category, acc);
  }
  return Array.from(map.entries())
    .map(([name, v]) => ({ name, ...v }))
    .sort((a, b) => b.foreground + b.interaction - (a.foreground + a.interaction));
}

/** Explains the three concepts that must never be conflated. */
export function UsageLegendNote() {
  return (
    <Notice tone="info">
      <strong>Open / running</strong> = the process exists. <strong>In foreground</strong> = its window was in front.{" "}
      <strong>Actively used</strong> = mouse/keyboard input while in front. An app can be open all day and never used.
    </Notice>
  );
}

export function AppUsageTable({ totals }: { totals: AppUsageTotal[] }) {
  const rows = useMemo(() => [...totals].sort((a, b) => b.interactionSeconds - a.interactionSeconds || b.foregroundSeconds - a.foregroundSeconds), [totals]);
  return (
    <div className="table-wrap">
      <table className="data-table">
        <thead>
          <tr>
            <th>Application</th>
            <th>Category</th>
            <th className="num" title="Process exists (open), regardless of use">Open / running</th>
            <th className="num" title="Window was the foreground window">In foreground</th>
            <th className="num" title="Mouse/keyboard input while in foreground">Actively used</th>
            <th className="num">Launches</th>
            <th className="num">Foreground switches</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((t) => (
            <tr key={`${t.processName}|${t.category}`}>
              <td className="font-medium">{t.processName}</td>
              <td>
                <span className="pill">{t.category}</span>
              </td>
              <td className="num"><Duration seconds={t.runningSeconds} /></td>
              <td className="num"><Duration seconds={t.foregroundSeconds} /></td>
              <td className="num font-semibold"><Duration seconds={t.interactionSeconds} /></td>
              <td className="num">{formatNumber(t.launches)}</td>
              <td className="num">{formatNumber(t.foregroundTransitions)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const PAGE_SIZE = 50;

/** Totals table + charts by application and category + foreground sessions table for any /applications endpoint. */
export function ApplicationActivityView({ path, query, showDevice = true }: { path: string; query: Query; showDevice?: boolean }) {
  const signature = JSON.stringify(query);
  const [page, setPage] = usePage(signature);
  const { data, error, loading, mutate } = useApi<ApplicationActivityResponse>(path, { ...query, page, pageSize: PAGE_SIZE });

  return (
    <DataState
      loading={loading}
      error={error}
      onRetry={() => void mutate()}
      isEmpty={!!data && data.totals.length === 0 && data.sessions.items.length === 0}
      emptyTitle="No application activity in this period"
      emptyHint="Applications appear once the session agent reports foreground activity."
    >
      {data && (
        <div className="space-y-5">
          <UsageLegendNote />
          <div className="grid gap-5 xl:grid-cols-2">
            <Card title="By application (top 12)">
              <DurationBars data={totalsToBars(data.totals)} series={APP_SERIES} label="Usage by application" />
            </Card>
            <Card title="By category">
              <DurationBars data={totalsByCategory(data.totals)} series={APP_SERIES} label="Usage by category" />
            </Card>
          </div>
          <Card title="Application totals" padded={false}>
            <AppUsageTable totals={data.totals} />
          </Card>
          <Card title="Foreground sessions" padded={false}>
            <div className="table-wrap !rounded-none !border-0">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Application</th>
                    <th>Window</th>
                    <th>Employee</th>
                    {showDevice && <th>Device</th>}
                    <th>Started</th>
                    <th>Ended</th>
                    <th className="num">In foreground</th>
                    <th className="num">Actively used</th>
                    <th className="num">Idle</th>
                  </tr>
                </thead>
                <tbody>
                  {data.sessions.items.map((s) => (
                    <tr key={s.id}>
                      <td className="font-medium">{s.processName}</td>
                      <td className="max-w-[260px] truncate text-muted" title={s.windowTitle ?? undefined}>{s.windowTitle ?? "—"}</td>
                      <td>{s.employeeCode ?? "—"}</td>
                      {showDevice && <td>{s.deviceId}</td>}
                      <td><Time value={s.startedAtUtc} /></td>
                      <td>{s.endedAtUtc ? <Time value={s.endedAtUtc} /> : <span className="pill pill-ok">In foreground now</span>}</td>
                      <td className="num"><Duration seconds={s.durationSeconds} /></td>
                      <td className="num"><Duration seconds={s.interactionSeconds} /></td>
                      <td className="num"><Duration seconds={s.idleSeconds} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="px-4 pb-3">
              <Pagination page={page} pageSize={PAGE_SIZE} total={data.sessions.total} onPage={setPage} />
            </div>
          </Card>
        </div>
      )}
    </DataState>
  );
}
