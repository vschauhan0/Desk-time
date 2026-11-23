"use client";

import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Card, Field, PageHeader } from "@/components/ui/primitives";
import { DataState, EmptyState } from "@/components/ui/states";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { useFilters } from "@/hooks/use-filters";
import { filtersToQuery } from "@/lib/filters";
import { ApplicationActivityView } from "@/features/shared/app-usage";
import { ProcessesView } from "@/features/shared/processes-view";
import { TimelineList } from "@/features/shared/timeline-list";
import type { EmployeeDto, Paged, TimelineItem } from "@/lib/types";

// ----- 5. Application Activity ---------------------------------------------------------------------

export function ApplicationsPage() {
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const query = useMemo(
    () => ({ ...dateRange.query, ...filtersToQuery(filters, ["employee", "department", "device", "application", "category"]) }),
    [dateRange.query, filters],
  );
  return (
    <>
      <PageHeader
        title="Application Activity"
        subtitle="Open time, foreground time and active use are reported separately. An application can be running without ever being used."
      />
      <FilterBar fields={["employee", "department", "device", "application", "category"]} value={filters} onChange={setFilters} dateRange={dateRange} />
      <ApplicationActivityView path="/applications" query={query} />
    </>
  );
}

// ----- 6. Process Timeline -------------------------------------------------------------------------

export function ProcessesPage() {
  const dateRange = useDateRange("today");
  const { filters, setFilters } = useFilters();
  const query = useMemo(
    () => ({ ...dateRange.query, ...filtersToQuery(filters, ["employee", "department", "device", "process", "status"], { process: "search" }) }),
    [dateRange.query, filters],
  );
  return (
    <>
      <PageHeader title="Process Timeline" subtitle="Lifetime of every monitored process: when it started, whether it is still running, and how long it lived." />
      <FilterBar
        fields={["employee", "department", "device", "process", "status"]}
        statusOptions={["RUNNING", "STOPPED"]}
        value={filters}
        onChange={setFilters}
        dateRange={dateRange}
      />
      <ProcessesView path="/processes" query={query} fromIso={dateRange.range.from.toISOString()} toIso={dateRange.range.to.toISOString()} />
    </>
  );
}

// ----- 7. Employee Timeline ------------------------------------------------------------------------

export function EmployeeTimelinePage() {
  const dateRange = useDateRange("today");
  const [employeeId, setEmployeeId] = useState("");
  const employees = useApi<Paged<EmployeeDto>>("/employees", { pageSize: 500 });
  const timeline = useApi<TimelineItem[]>(employeeId ? `/employees/${encodeURIComponent(employeeId)}/timeline` : null, {
    from: dateRange.query.from,
    to: dateRange.query.to,
  });

  return (
    <>
      <PageHeader title="Employee Timeline" subtitle="Login, lock, idle and application events for one employee across all of their devices, in your local time." />
      <FilterBar
        fields={[]}
        value={{}}
        onChange={() => undefined}
        dateRange={dateRange}
        extra={
          <div className="w-64">
            <Field label="Employee">
              <select className="field" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)} aria-label="Employee">
                <option value="">Choose an employee...</option>
                {(employees.data?.items ?? []).map((e) => (
                  <option key={e.id} value={e.id}>{e.fullName} ({e.employeeCode})</option>
                ))}
              </select>
            </Field>
          </div>
        }
      />
      {!employeeId ? (
        <div className="card"><EmptyState title="Choose an employee" hint="Pick an employee and a period to see their timeline." /></div>
      ) : (
        <DataState loading={timeline.loading} error={timeline.error} onRetry={() => void timeline.mutate()}>
          <Card><TimelineList items={timeline.data ?? []} /></Card>
        </DataState>
      )}
    </>
  );
}
