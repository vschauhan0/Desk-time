"use client";

import { Download, Printer } from "lucide-react";
import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Button, Field, Notice, PageHeader } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { formatDateTime, humanize, toDateInput } from "@/lib/format";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { useFilters } from "@/hooks/use-filters";
import { api, saveBlob } from "@/lib/api";
import { filtersToQuery } from "@/lib/filters";
import { REPORT_KINDS, type ReportKind, type ReportResult } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";
import { ReportTable } from "./report-table";

export const REPORT_LABELS: Record<ReportKind, string> = {
  daily: "Daily summary",
  attendance: "Attendance (login / logout / worked)",
  "application-usage": "Application usage",
  "active-idle": "Active / idle / locked",
  "device-health": "Device health",
  "process-timeline": "Process timeline",
  usb: "USB events",
  "agent-health": "Agent health",
  "department-summary": "Department summary",
};

export function ReportsPage() {
  const { isAdmin } = useAuth();
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const [kind, setKind] = useState<ReportKind>("daily");

  const query = useMemo(
    () => ({
      ...dateRange.query,
      ...filtersToQuery(filters, ["employee", "department", "device"]),
      ...(kind === "daily" ? { date: toDateInput(dateRange.range.from) } : {}),
    }),
    [dateRange.query, dateRange.range.from, filters, kind],
  );
  const { data, error, loading, mutate } = useApi<ReportResult>(`/reports/${kind}`, { ...query, format: "json" });
  const csv = useAction(async () => {
    const { blob, filename } = await api.reportCsv(kind, query);
    saveBlob(blob, filename);
  });

  return (
    <>
      <PageHeader
        title="Reports"
        subtitle="Objective telemetry only: durations, counts and states. Reports never rate or score people."
        actions={
          <>
            <Button icon={Printer} onClick={() => window.print()} disabled={!data}>Print</Button>
            {isAdmin && (
              <Button variant="primary" icon={Download} loading={csv.pending} disabled={!data} onClick={() => void csv.run()}>
                Download CSV
              </Button>
            )}
          </>
        }
      />
      <FilterBar
        fields={["employee", "department", "device"]}
        value={filters}
        onChange={setFilters}
        dateRange={dateRange}
        extra={
          <div className="w-56">
            <Field label="Report">
              <select className="field" value={kind} onChange={(e) => setKind(e.target.value as ReportKind)} aria-label="Report kind">
                {REPORT_KINDS.map((k) => <option key={k} value={k}>{REPORT_LABELS[k]}</option>)}
              </select>
            </Field>
          </div>
        }
      />
      {csv.error && <div className="mb-3"><Notice tone="danger">{csv.error}</Notice></div>}
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.rows.length === 0} emptyTitle="This report has no rows for the selected filters">
        {data && (
          <section aria-label={data.title}>
            <div className="mb-3">
              <h2 className="text-[15px] font-semibold">{data.title || humanize(data.kind)}</h2>
              <p className="text-[12.5px] text-muted">
                {formatDateTime(data.fromUtc)} to {formatDateTime(data.toUtc)} (your local time) - {data.rows.length} row{data.rows.length === 1 ? "" : "s"}
              </p>
            </div>
            <ReportTable report={data} />
          </section>
        )}
      </DataState>
    </>
  );
}
