"use client";

import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Field, Notice, PageHeader, Pill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Pagination, Time } from "@/components/ui/widgets";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { useFilters } from "@/hooks/use-filters";
import { usePage } from "@/hooks/use-page";
import { filtersToQuery } from "@/lib/filters";
import { humanize } from "@/lib/format";
import type { FileEventDto, Paged, PolicyDto, UsbEventDto } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

const PAGE_SIZE = 100;

// ----- 10. USB events ------------------------------------------------------------------------------

export function UsbPage() {
  const dateRange = useDateRange("last7");
  const { filters, setFilters } = useFilters();
  const query = useMemo(() => ({ ...dateRange.query, ...filtersToQuery(filters, ["device", "employee", "department"]) }), [dateRange.query, filters]);
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<UsbEventDto>>("/usb-events", { ...query, page, pageSize: PAGE_SIZE });

  return (
    <>
      <PageHeader title="USB Events" subtitle="Removable and peripheral devices connected to or removed from monitored PCs (metadata only, never file contents)." />
      <FilterBar fields={["employee", "department", "device"]} value={filters} onChange={setFilters} dateRange={dateRange} />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No USB events in this period" emptyHint="USB monitoring can be turned off in the policy (usbMonitoringEnabled).">
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead><tr><th>Time</th><th>Event</th><th>Device</th><th>Employee</th><th>Type</th><th>Vendor</th><th>Product</th><th>Serial number</th></tr></thead>
                <tbody>
                  {data.items.map((e) => (
                    <tr key={e.id}>
                      <td className="whitespace-nowrap"><Time value={e.timestampUtc} /></td>
                      <td><Pill tone={e.eventType === "USB_CONNECTED" ? "info" : "neutral"}>{e.eventType === "USB_CONNECTED" ? "Connected" : e.eventType === "USB_DISCONNECTED" ? "Disconnected" : humanize(e.eventType)}</Pill></td>
                      <td>{e.deviceId}</td>
                      <td>{e.employeeCode ?? "—"}</td>
                      <td>{e.deviceType}</td>
                      <td>{e.vendor ?? "—"}</td>
                      <td>{e.product ?? "—"}</td>
                      <td className="mono">{e.serialNumber ?? "—"}</td>
                    </tr>
                  ))}
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

// ----- 11. File activity ---------------------------------------------------------------------------

const FILE_ACTIONS = ["CREATED", "MODIFIED", "RENAMED", "DELETED"];

export function FilesPage() {
  const { isAdmin } = useAuth();
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const [action, setAction] = useState("");
  const query = useMemo(() => ({ ...dateRange.query, ...filtersToQuery(filters, ["device", "employee", "search"]), action }), [dateRange.query, filters, action]);
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<FileEventDto>>("/file-events", { ...query, page, pageSize: PAGE_SIZE });
  // Policy state is only readable by ADMIN+; used to explain an empty list.
  const policies = useApi<PolicyDto[]>(isAdmin ? "/settings/policies" : null);
  const enabledIn = policies.data?.filter((p) => p.policy.fileMonitoring.enabled) ?? [];

  return (
    <>
      <PageHeader title="File Activity" subtitle="File create / modify / rename / delete metadata for the folders a policy selects. Contents are never collected." />
      <div className="mb-4">
        {isAdmin && policies.data ? (
          enabledIn.length > 0 ? (
            <Notice tone="ok">File monitoring is enabled by policy: {enabledIn.map((p) => p.name).join(", ")}.</Notice>
          ) : (
            <Notice tone="warn">File monitoring is <strong>disabled by default</strong> and no policy enables it, so no file events are collected. Enable it under Settings, Policies, File monitoring.</Notice>
          )
        ) : (
          <Notice tone="info">File monitoring is disabled by default and only records events when an administrator enables it in a policy.</Notice>
        )}
      </div>
      <FilterBar
        fields={["employee", "device", "search"]}
        value={filters}
        onChange={setFilters}
        dateRange={dateRange}
        searchPlaceholder="File name or path..."
        extra={
          <div className="w-40">
            <Field label="Action">
              <select className="field" value={action} onChange={(e) => setAction(e.target.value)} aria-label="Action">
                <option value="">All actions</option>
                {FILE_ACTIONS.map((a) => <option key={a} value={a}>{humanize(a)}</option>)}
              </select>
            </Field>
          </div>
        }
      />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No file events in this period" emptyHint="Expected when file monitoring is disabled (the default).">
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead><tr><th>Time</th><th>Action</th><th>File</th><th>Path</th><th>Device</th><th>Employee</th><th>User</th></tr></thead>
                <tbody>
                  {data.items.map((e) => (
                    <tr key={e.id}>
                      <td className="whitespace-nowrap"><Time value={e.timestampUtc} /></td>
                      <td><span className="pill">{humanize(e.action)}</span></td>
                      <td className="font-medium">{e.fileName}</td>
                      <td className="mono max-w-[360px] truncate text-muted" title={e.path}>{e.path}</td>
                      <td>{e.deviceId}</td>
                      <td>{e.employeeCode ?? "—"}</td>
                      <td>{e.userName ?? "—"}</td>
                    </tr>
                  ))}
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
