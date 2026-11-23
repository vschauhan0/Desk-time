"use client";

import { KeyRound } from "lucide-react";
import Link from "next/link";
import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Button, PageHeader, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { MiniBar, Pagination, RelativeTime } from "@/components/ui/widgets";
import { useApi } from "@/hooks/use-api";
import { useFilters } from "@/hooks/use-filters";
import { usePage } from "@/hooks/use-page";
import { filtersToQuery } from "@/lib/filters";
import { deviceStatusTone, loadTone, presenceTone } from "@/lib/status";
import { DEVICE_STATUSES, type DeviceSummary, type Paged } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";
import { DeviceActions } from "./device-actions";
import { CreateTokenDialog } from "./enrollment-dialogs";

const PAGE_SIZE = 50;

export function DevicesPage() {
  const { isAdmin } = useAuth();
  const { filters, setFilters } = useFilters();
  const query = useMemo(() => filtersToQuery(filters, ["status", "employee", "department", "search"]), [filters]);
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<DeviceSummary>>("/devices", { ...query, page, pageSize: PAGE_SIZE }, { refreshInterval: 30_000 });
  const [tokenOpen, setTokenOpen] = useState(false);

  return (
    <>
      <PageHeader
        title="Devices"
        subtitle="Every enrolled PC. Open a device for activity, applications, processes, metrics and diagnostics."
        actions={isAdmin && <Button variant="primary" icon={KeyRound} onClick={() => setTokenOpen(true)}>Create enrollment token</Button>}
      />
      <FilterBar fields={["status", "employee", "department", "search"]} value={filters} onChange={setFilters} statusOptions={DEVICE_STATUSES} searchPlaceholder="Host, device id, employee..." />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No devices found" emptyHint={isAdmin ? "Create an enrollment token and install the agent on a PC." : "No devices match the current filters."}>
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Device</th>
                    <th>Employee</th>
                    <th>Department</th>
                    <th>Status</th>
                    <th>Presence</th>
                    <th>Current application</th>
                    <th>CPU</th>
                    <th>RAM</th>
                    <th>Last seen</th>
                    <th>Agent</th>
                    {isAdmin && <th className="no-print">Actions</th>}
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((d) => (
                    <tr key={d.id}>
                      <td className="whitespace-nowrap">
                        <Link href={`/devices/${encodeURIComponent(d.deviceId)}`} className="font-medium text-brand hover:underline">{d.hostname}</Link>
                        <p className="mono text-faint">{d.deviceId}</p>
                      </td>
                      <td>{d.employeeName ?? "—"}{d.employeeCode && <span className="text-faint"> ({d.employeeCode})</span>}</td>
                      <td>{d.department ?? "—"}</td>
                      <td><StatusPill value={d.status} tone={deviceStatusTone(d.status)} /></td>
                      <td><StatusPill value={d.presence} tone={presenceTone(d.presence)} /></td>
                      <td className="max-w-[220px] truncate" title={d.currentWindow ?? undefined}>{d.currentApplication ?? "—"}</td>
                      <td><MiniBar percent={d.cpuPercent} tone={loadTone(d.cpuPercent)} /></td>
                      <td><MiniBar percent={d.memoryUsedPercent} tone={loadTone(d.memoryUsedPercent)} /></td>
                      <td><RelativeTime value={d.lastSeenUtc} /></td>
                      <td className="mono">{d.agentVersion}</td>
                      {isAdmin && (
                        <td className="no-print"><DeviceActions deviceId={d.deviceId} status={d.status} onChanged={() => void mutate()} /></td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={page} pageSize={PAGE_SIZE} total={data.total} onPage={setPage} />
          </>
        )}
      </DataState>
      <CreateTokenDialog open={tokenOpen} onClose={() => setTokenOpen(false)} />
    </>
  );
}
