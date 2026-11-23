"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { PageHeader, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { MiniBar, Pagination, RelativeTime, Sparkline } from "@/components/ui/widgets";
import { useLiveCards } from "@/features/live/use-live-cards";
import { useApi } from "@/hooks/use-api";
import { useFilters } from "@/hooks/use-filters";
import { usePage } from "@/hooks/use-page";
import { filterLiveCards } from "@/lib/filters";
import { formatNumber } from "@/lib/format";
import { findKey } from "@/lib/report-utils";
import { deviceStatusTone, loadTone } from "@/lib/status";
import { DEVICE_STATUSES, type LiveDeviceCard, type ReportResult, type SystemMetricDto } from "@/lib/types";

const PAGE_SIZE = 25;

interface Extra {
  disk: number | null;
  queue: number | null;
}

/** Last-hour CPU sparkline, fetched lazily per visible row (SWR de-duplicates and caches). */
function CpuSpark({ deviceId }: { deviceId: string }) {
  const [from] = useState(() => new Date(Math.floor(Date.now() / 300_000) * 300_000 - 3_600_000).toISOString());
  const { data } = useApi<SystemMetricDto[]>(`/devices/${encodeURIComponent(deviceId)}/metrics`, { from, maxPoints: 24 }, { refreshInterval: 60_000 });
  const values = (data ?? []).map((m) => m.cpuPercent);
  return <Sparkline values={values} max={100} />;
}

function num(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

export function DeviceHealthPage() {
  const { cards, loaded, loading, error, mutate } = useLiveCards();
  const { filters, setFilters } = useFilters();
  // Disk and queue depth are not part of the live card; the device-health report carries them when available.
  const report = useApi<ReportResult>("/reports/device-health", { format: "json" }, { refreshInterval: 60_000 });

  const extras = useMemo(() => {
    const map = new Map<string, Extra>();
    const r = report.data;
    if (!r) return map;
    const idKey = findKey(r.columns, /^(deviceId|device|hostname)$/i) ?? findKey(r.columns, /device|host/i);
    const diskKey = findKey(r.columns, /disk/i);
    const queueKey = findKey(r.columns, /queue/i);
    if (!idKey) return map;
    for (const row of r.rows) {
      const id = row[idKey];
      if (typeof id === "string") map.set(id, { disk: diskKey ? num(row[diskKey]) : null, queue: queueKey ? num(row[queueKey]) : null });
    }
    return map;
  }, [report.data]);

  const visible = useMemo(() => filterLiveCards(cards, filters).sort((a, b) => a.hostname.localeCompare(b.hostname)), [cards, filters]);
  const [page, setPage] = usePage(JSON.stringify(filters));
  const pageRows = visible.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);
  const extraFor = (c: LiveDeviceCard): Extra | undefined => extras.get(c.deviceId) ?? extras.get(c.hostname);

  return (
    <>
      <PageHeader title="Device Health" subtitle="Fleet status, heartbeat freshness, resource use and agent version. Open a device's diagnostics for collectors and queue details." />
      <FilterBar fields={["status", "department", "search"]} value={filters} onChange={setFilters} statusOptions={DEVICE_STATUSES} searchPlaceholder="Host or device id..." />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={loaded && visible.length === 0} emptyTitle="No devices to show">
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th>Device</th>
                <th>Status</th>
                <th>Last heartbeat</th>
                <th>CPU</th>
                <th title="CPU over the last hour">CPU (1 h)</th>
                <th>RAM</th>
                <th>Disk</th>
                <th>Agent</th>
                <th className="num">Queue</th>
                <th className="no-print" />
              </tr>
            </thead>
            <tbody>
              {pageRows.map((c) => {
                const x = extraFor(c);
                return (
                  <tr key={c.deviceId}>
                    <td>
                      <Link href={`/devices/${encodeURIComponent(c.deviceId)}`} className="font-medium text-brand hover:underline">{c.hostname}</Link>
                      <p className="text-[12px] text-muted">{c.employeeName ?? c.deviceId}</p>
                    </td>
                    <td><StatusPill value={c.status} tone={deviceStatusTone(c.status)} /></td>
                    <td><RelativeTime value={c.lastHeartbeatUtc} /></td>
                    <td><MiniBar percent={c.cpuPercent} tone={loadTone(c.cpuPercent)} /></td>
                    <td><CpuSpark deviceId={c.deviceId} /></td>
                    <td><MiniBar percent={c.memoryUsedPercent} tone={loadTone(c.memoryUsedPercent)} /></td>
                    <td><MiniBar percent={x?.disk ?? null} tone={loadTone(x?.disk)} /></td>
                    <td className="mono">{c.agentVersion}</td>
                    <td className="num">{x?.queue != null ? formatNumber(x.queue) : "—"}</td>
                    <td className="no-print text-right">
                      <Link href={`/devices/${encodeURIComponent(c.deviceId)}?tab=diagnostics`} className="text-[12.5px] text-brand hover:underline">Diagnostics</Link>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <Pagination page={page} pageSize={PAGE_SIZE} total={visible.length} onPage={setPage} />
      </DataState>
    </>
  );
}
