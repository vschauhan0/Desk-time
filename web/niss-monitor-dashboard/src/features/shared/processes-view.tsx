"use client";

import { useMemo } from "react";
import { Card } from "@/components/ui/primitives";
import { Pill } from "@/components/ui/primitives";
import { DataState, EmptyState } from "@/components/ui/states";
import { Duration, Pagination, Time } from "@/components/ui/widgets";
import { useApi } from "@/hooks/use-api";
import { useNow } from "@/hooks/use-now";
import { usePage } from "@/hooks/use-page";
import type { Query } from "@/lib/api";
import { formatDateTime, formatDuration, formatTime, formatUtc } from "@/lib/format";
import { layoutGantt } from "@/lib/gantt";
import type { Paged, ProcessSessionDto } from "@/lib/types";

const PAGE_SIZE = 100;
const GANTT_ROWS = 40;

function ProcessGantt({ sessions, fromIso, toIso }: { sessions: ProcessSessionDto[]; fromIso: string; toIso: string }) {
  const now = useNow();
  const rows = useMemo(() => [...sessions].sort((a, b) => Date.parse(a.startedAtUtc) - Date.parse(b.startedAtUtc)).slice(0, GANTT_ROWS), [sessions]);
  const layout = useMemo(() => layoutGantt(rows, Date.parse(fromIso), Date.parse(toIso), now), [rows, fromIso, toIso, now]);

  if (rows.length === 0) return <EmptyState title="No processes to draw" />;
  return (
    <div className="overflow-x-auto">
      <div className="min-w-[640px]" role="img" aria-label="Process lifetimes on a time axis">
        <div className="flex">
          <div className="w-44 shrink-0" />
          <div className="relative h-6 grow border-b border-line">
            {layout.ticks.map((t) => (
              <span key={t.pct} className={`absolute top-0 text-[10.5px] text-faint tabular-nums ${t.pct === 0 ? "" : t.pct === 100 ? "-translate-x-full" : "-translate-x-1/2"}`} style={{ left: `${t.pct}%` }} title={formatUtc(new Date(t.ms))}>
                {formatDateTime(new Date(t.ms)).slice(0, 6)} {formatTime(new Date(t.ms), false)}
              </span>
            ))}
          </div>
        </div>
        {layout.bars.map((b) => (
          <div key={b.session.id} className="flex items-center border-b border-line/60 hover:bg-surface-2">
            <div className="w-44 shrink-0 truncate py-1 pr-2 text-[12px]" title={`${b.session.processName} (PID ${b.session.processId})`}>
              {b.session.processName} <span className="text-faint">#{b.session.processId}</span>
            </div>
            <div className="relative h-6 grow">
              {layout.ticks.map((t) => (
                <span key={t.pct} className="absolute inset-y-0 w-px bg-line/70" style={{ left: `${t.pct}%` }} aria-hidden />
              ))}
              <span
                className="absolute top-1.5 h-3 rounded-sm"
                style={{
                  left: `${b.leftPct}%`,
                  width: `${b.widthPct}%`,
                  background: b.open
                    ? "repeating-linear-gradient(135deg, var(--c3) 0 4px, transparent 4px 7px)"
                    : "var(--c1)",
                  border: b.open ? "1px solid var(--c3)" : undefined,
                }}
                title={`${b.session.processName} (PID ${b.session.processId})\n${formatDateTime(b.session.startedAtUtc)} - ${b.session.endedAtUtc ? formatDateTime(b.session.endedAtUtc) : "still running"}\n${formatDuration(b.session.durationSeconds)}`}
              />
            </div>
          </div>
        ))}
      </div>
      <p className="mt-2 flex flex-wrap items-center gap-4 text-[11.5px] text-muted">
        <span className="flex items-center gap-1.5">
          <span className="inline-block h-2.5 w-5 rounded-sm" style={{ background: "var(--c1)" }} aria-hidden /> Stopped
        </span>
        <span className="flex items-center gap-1.5">
          <span className="inline-block h-2.5 w-5 rounded-sm" style={{ border: "1px solid var(--c3)", background: "repeating-linear-gradient(135deg, var(--c3) 0 3px, transparent 3px 6px)" }} aria-hidden /> Still running
        </span>
        {sessions.length > GANTT_ROWS && <span>Showing the first {GANTT_ROWS} of {sessions.length} on this page (earliest start first).</span>}
      </p>
    </div>
  );
}

interface Props {
  path: string;
  query: Query;
  fromIso: string;
  toIso: string;
  showDevice?: boolean;
}

/** Process table + Gantt-like timeline for /processes or /devices/{id}/processes. */
export function ProcessesView({ path, query, fromIso, toIso, showDevice = true }: Props) {
  const signature = JSON.stringify(query);
  const [page, setPage] = usePage(signature);
  const { data, error, loading, mutate } = useApi<Paged<ProcessSessionDto>>(path, { ...query, page, pageSize: PAGE_SIZE });

  return (
    <DataState
      loading={loading}
      error={error}
      onRetry={() => void mutate()}
      isEmpty={!!data && data.items.length === 0}
      emptyTitle="No processes in this period"
      emptyHint="By default only interactive user processes are monitored (policy: process monitoring mode)."
    >
      {data && (
        <div className="space-y-5">
          <Card title="Timeline">
            <ProcessGantt sessions={data.items} fromIso={fromIso} toIso={toIso} />
          </Card>
          <Card title="Processes" padded={false}>
            <div className="table-wrap !rounded-none !border-0">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Process</th>
                    <th className="num">PID</th>
                    <th className="num">Parent PID</th>
                    <th>User</th>
                    {showDevice && <th>Device</th>}
                    <th>Executable</th>
                    <th>Start</th>
                    <th>End</th>
                    <th className="num">Duration</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((p) => (
                    <tr key={p.id}>
                      <td className="font-medium">{p.processName}</td>
                      <td className="num">{p.processId}</td>
                      <td className="num">{p.parentProcessId ?? "—"}</td>
                      <td>{p.userName ?? "—"}</td>
                      {showDevice && <td>{p.deviceId}</td>}
                      <td className="mono max-w-[260px] truncate text-muted" title={p.executablePath ?? undefined}>{p.executablePath ?? "—"}</td>
                      <td><Time value={p.startedAtUtc} /></td>
                      <td><Time value={p.endedAtUtc} /></td>
                      <td className="num"><Duration seconds={p.durationSeconds} /></td>
                      <td>
                        <Pill tone={p.status === "RUNNING" ? "ok" : "neutral"}>{p.status === "RUNNING" ? "Running" : "Stopped"}</Pill>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="px-4 pb-3">
              <Pagination page={page} pageSize={PAGE_SIZE} total={data.total} onPage={setPage} />
            </div>
          </Card>
        </div>
      )}
    </DataState>
  );
}
