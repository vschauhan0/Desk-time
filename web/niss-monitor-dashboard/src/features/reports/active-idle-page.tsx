"use client";

import { useMemo } from "react";
import { DurationBars, PRESENCE_COLORS } from "@/components/charts/charts";
import { FilterBar } from "@/components/filters/filter-bar";
import { Card, Notice, PageHeader } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { useFilters } from "@/hooks/use-filters";
import { filtersToQuery } from "@/lib/filters";
import { extractActiveIdle } from "@/lib/report-utils";
import type { ReportResult } from "@/lib/types";
import { ReportTable } from "./report-table";

export function ActiveIdlePage() {
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const query = useMemo(
    () => ({ ...dateRange.query, ...filtersToQuery(filters, ["employee", "department", "device"]), format: "json" }),
    [dateRange.query, filters],
  );
  const { data, error, loading, mutate } = useApi<ReportResult>("/reports/active-idle", query);
  const rows = useMemo(() => (data ? extractActiveIdle(data) : null), [data]);
  const bars = useMemo(
    () =>
      (rows ?? [])
        .map((r) => ({ name: r.name, active: r.activeSeconds, idle: r.idleSeconds, locked: r.lockedSeconds }))
        .sort((a, b) => b.active + b.idle + b.locked - (a.active + a.idle + a.locked))
        .slice(0, 25),
    [rows],
  );

  return (
    <>
      <PageHeader title="Idle / Active Report" subtitle="Per-employee split of active, idle and locked time. Objective telemetry, not a performance rating." />
      <FilterBar fields={["employee", "department", "device"]} value={filters} onChange={setFilters} dateRange={dateRange} />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.rows.length === 0} emptyTitle="No presence data for this period">
        {data && (
          <div className="space-y-5">
            {rows === null ? (
              <Notice tone="warn">The report columns were not recognised as active/idle/locked durations, so only the table is shown.</Notice>
            ) : (
              <Card title={`Active, idle and locked time per employee${(rows?.length ?? 0) > 25 ? " (top 25 by logged-in time)" : ""}`}>
                <DurationBars
                  stacked
                  data={bars}
                  label="Active, idle and locked time per employee"
                  series={[
                    { key: "active", label: "Active", color: PRESENCE_COLORS.active },
                    { key: "idle", label: "Idle", color: PRESENCE_COLORS.idle },
                    { key: "locked", label: "Locked", color: PRESENCE_COLORS.locked },
                  ]}
                />
              </Card>
            )}
            <ReportTable report={data} />
          </div>
        )}
      </DataState>
    </>
  );
}
