"use client";

import { ArrowLeft } from "lucide-react";
import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { useEffect, useMemo, type ReactNode } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { PercentLines } from "@/components/charts/charts";
import { Card, PageHeader, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Pagination, RelativeTime, Tabs, Time, type TabDef } from "@/components/ui/widgets";
import { DeviceCard } from "@/features/live/device-card";
import { EventFeed } from "@/features/live/event-feed";
import { ApplicationActivityView } from "@/features/shared/app-usage";
import { ProcessesView } from "@/features/shared/processes-view";
import { TimelineList } from "@/features/shared/timeline-list";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { useFilters } from "@/hooks/use-filters";
import { usePage } from "@/hooks/use-page";
import { describeEvent } from "@/lib/events";
import { filtersToQuery } from "@/lib/filters";
import { formatDateTime, formatGigabytes, formatMegabytes, humanize } from "@/lib/format";
import { deviceStatusTone } from "@/lib/status";
import type { DeviceDetail as DeviceDetailDto, EventDto, Paged, SystemMetricDto, TimelineItem } from "@/lib/types";
import { useMonitorHub } from "@/providers/signalr-provider";
import { useLiveStore } from "@/store/live-store";
import { DeviceActions } from "./device-actions";
import { DiagnosticsPanel } from "./diagnostics-panel";

type TabKey = "overview" | "activity" | "applications" | "processes" | "metrics" | "timeline" | "diagnostics";

const TABS: TabDef<TabKey>[] = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity" },
  { key: "applications", label: "Applications" },
  { key: "processes", label: "Processes" },
  { key: "metrics", label: "Metrics" },
  { key: "timeline", label: "Timeline" },
  { key: "diagnostics", label: "Diagnostics" },
];

function isTab(v: string | null): v is TabKey {
  return TABS.some((t) => t.key === v);
}

function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-line py-2 last:border-0">
      <dt className="text-[12.5px] text-muted">{label}</dt>
      <dd className="min-w-0 break-words text-right text-[13px]">{children}</dd>
    </div>
  );
}

function OverviewTab({ detail }: { detail: DeviceDetailDto }) {
  const { subscribeDevice } = useMonitorHub();
  const live = useLiveStore((s) => s.cards[detail.summary.deviceId]) ?? detail.live;
  const deviceId = detail.summary.deviceId;
  useEffect(() => subscribeDevice(deviceId), [subscribeDevice, deviceId]);

  return (
    <div className="grid gap-5 xl:grid-cols-[minmax(0,420px)_minmax(0,1fr)]">
      <div className="space-y-5">
        <DeviceCard card={live} defaultExpanded />
        <Card title="Hardware & network">
          <dl>
            <InfoRow label="Operating system">{detail.osVersion ?? detail.summary.operatingSystem ?? "—"}</InfoRow>
            <InfoRow label="Manufacturer / model">{[detail.manufacturer, detail.model].filter(Boolean).join(" ") || "—"}</InfoRow>
            <InfoRow label="Serial number"><span className="mono">{detail.machineSerial ?? "—"}</span></InfoRow>
            <InfoRow label="Windows users">{detail.windowsUsers.join(", ") || "—"}</InfoRow>
            <InfoRow label="IP addresses"><span className="mono">{detail.ipAddresses.join(", ") || "—"}</span></InfoRow>
            <InfoRow label="MAC addresses"><span className="mono">{detail.macAddresses.join(", ") || "—"}</span></InfoRow>
            <InfoRow label="Last boot"><Time value={detail.lastBootUtc} /></InfoRow>
            <InfoRow label="Enrolled"><Time value={detail.summary.createdAtUtc} /></InfoRow>
          </dl>
        </Card>
      </div>
      <EventFeed deviceId={deviceId} height="max-h-[60vh]" />
    </div>
  );
}

const PAGE_SIZE = 100;

function ActivityTab({ deviceId }: { deviceId: string }) {
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const query = useMemo(() => ({ ...dateRange.query, ...filtersToQuery(filters, ["eventType"]) }), [dateRange.query, filters]);
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<EventDto>>(`/devices/${encodeURIComponent(deviceId)}/activity`, { ...query, page, pageSize: PAGE_SIZE });
  return (
    <>
      <FilterBar fields={["eventType"]} value={filters} onChange={setFilters} dateRange={dateRange} />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No events in this period">
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead><tr><th>Time</th><th>Event</th><th>Details</th><th>Employee</th><th>Source</th></tr></thead>
                <tbody>
                  {data.items.map((e) => {
                    const d = describeEvent(e);
                    return (
                      <tr key={e.eventId}>
                        <td className="whitespace-nowrap"><Time value={e.timestampUtc} /></td>
                        <td><span className="pill">{humanize(e.eventType)}</span></td>
                        <td><span className="font-medium">{d.title}</span>{d.detail && <span className="text-muted"> - {d.detail}</span>}</td>
                        <td>{e.employeeCode ?? "—"}</td>
                        <td className="text-muted">{e.source}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <Pagination page={page} pageSize={PAGE_SIZE} total={data.total} onPage={setPage} />
          </>
        )}
      </DataState>
    </>
  );
}

function ApplicationsTab({ deviceId }: { deviceId: string }) {
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const query = useMemo(() => ({ ...dateRange.query, ...filtersToQuery(filters, ["application"]) }), [dateRange.query, filters]);
  return (
    <>
      <FilterBar fields={["application"]} value={filters} onChange={setFilters} dateRange={dateRange} />
      <ApplicationActivityView path={`/devices/${encodeURIComponent(deviceId)}/applications`} query={query} showDevice={false} />
    </>
  );
}

function ProcessesTab({ deviceId }: { deviceId: string }) {
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const query = useMemo(() => ({ ...dateRange.query, ...filtersToQuery(filters, ["status", "process"], { process: "search" }) }), [dateRange.query, filters]);
  return (
    <>
      <FilterBar fields={["process", "status"]} statusOptions={["RUNNING", "STOPPED"]} value={filters} onChange={setFilters} dateRange={dateRange} />
      <ProcessesView path={`/devices/${encodeURIComponent(deviceId)}/processes`} query={query} fromIso={dateRange.range.from.toISOString()} toIso={dateRange.range.to.toISOString()} showDevice={false} />
    </>
  );
}

function MetricsTab({ deviceId }: { deviceId: string }) {
  const dateRange = useDateRange("today");
  const query = useMemo(() => ({ ...dateRange.query, maxPoints: 300 }), [dateRange.query]);
  const { data, error, loading, mutate } = useApi<SystemMetricDto[]>(`/devices/${encodeURIComponent(deviceId)}/metrics`, query, { refreshInterval: 60_000 });
  const points = useMemo(
    () =>
      (data ?? []).map((m) => ({
        t: Date.parse(m.timestampUtc),
        cpu: m.cpuPercent,
        ram: m.memoryUsedPercent,
        ...(m.diskUsedPercent !== null ? { disk: m.diskUsedPercent } : {}),
      })),
    [data],
  );
  const last = data && data.length > 0 ? data[data.length - 1] : null;
  return (
    <>
      <FilterBar fields={[]} value={{}} onChange={() => undefined} dateRange={dateRange} />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.length === 0} emptyTitle="No metrics in this period" emptyHint="System metrics are sampled by the agent service every 30 seconds by default.">
        <div className="space-y-5">
          <Card title="CPU, memory and disk utilisation">
            <PercentLines
              data={points}
              series={[
                { key: "cpu", label: "CPU", color: "var(--c1)" },
                { key: "ram", label: "Memory", color: "var(--c2)" },
                { key: "disk", label: "Disk", color: "var(--c3)" },
              ]}
            />
          </Card>
          {last && (
            <Card title={`Latest sample - ${formatDateTime(last.timestampUtc)}`}>
              <dl className="grid grid-cols-2 gap-4 text-[13px] sm:grid-cols-4">
                <div><dt className="label">Memory available</dt><dd>{formatMegabytes(last.memoryAvailableMb)}</dd></div>
                <div><dt className="label">Disk free</dt><dd>{formatGigabytes(last.diskFreeGb)}</dd></div>
                <div><dt className="label">Battery</dt><dd>{last.batteryPercent !== null ? `${last.batteryPercent}%` : "n/a"}</dd></div>
                <div><dt className="label">Uptime</dt><dd>{Math.floor(last.uptimeSeconds / 3600)} h</dd></div>
              </dl>
            </Card>
          )}
        </div>
      </DataState>
    </>
  );
}

function TimelineTab({ deviceId }: { deviceId: string }) {
  const dateRange = useDateRange("today");
  const { data, error, loading, mutate } = useApi<TimelineItem[]>(`/devices/${encodeURIComponent(deviceId)}/timeline`, dateRange.query);
  return (
    <>
      <FilterBar fields={[]} value={{}} onChange={() => undefined} dateRange={dateRange} />
      <DataState loading={loading} error={error} onRetry={() => void mutate()}>
        <Card><TimelineList items={data ?? []} showDevice={false} /></Card>
      </DataState>
    </>
  );
}

export function DeviceDetail() {
  const params = useParams<{ deviceId: string }>();
  const deviceId = decodeURIComponent(params.deviceId);
  const router = useRouter();
  const search = useSearchParams();
  const tabParam = search.get("tab");
  const tab: TabKey = isTab(tabParam) ? tabParam : "overview";
  const { data, error, loading, mutate } = useApi<DeviceDetailDto>(`/devices/${encodeURIComponent(deviceId)}`, undefined, { refreshInterval: 30_000 });

  const s = data?.summary;
  return (
    <>
      <PageHeader
        title={s ? s.hostname : deviceId}
        subtitle={
          s && (
            <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <span className="mono">{s.deviceId}</span>
              <StatusPill value={s.status} tone={deviceStatusTone(s.status)} />
              <span>{s.employeeName ?? "Unassigned"}{s.department ? ` - ${s.department}` : ""}</span>
              <span>Last seen <RelativeTime value={s.lastSeenUtc} /></span>
            </span>
          )
        }
        actions={
          <>
            <Link href="/devices" className="btn"><ArrowLeft size={14} aria-hidden /> Devices</Link>
            {s && <DeviceActions deviceId={s.deviceId} status={s.status} size="md" onChanged={() => void mutate()} />}
          </>
        }
      />
      <Tabs tabs={TABS} value={tab} onChange={(k) => router.replace(k === "overview" ? `/devices/${encodeURIComponent(deviceId)}` : `/devices/${encodeURIComponent(deviceId)}?tab=${k}`)} />
      <DataState loading={loading} error={error} onRetry={() => void mutate()}>
        {data && (
          <div role="tabpanel">
            {tab === "overview" && <OverviewTab detail={data} />}
            {tab === "activity" && <ActivityTab deviceId={deviceId} />}
            {tab === "applications" && <ApplicationsTab deviceId={deviceId} />}
            {tab === "processes" && <ProcessesTab deviceId={deviceId} />}
            {tab === "metrics" && <MetricsTab deviceId={deviceId} />}
            {tab === "timeline" && <TimelineTab deviceId={deviceId} />}
            {tab === "diagnostics" && <DiagnosticsPanel deviceId={deviceId} />}
          </div>
        )}
      </DataState>
    </>
  );
}
