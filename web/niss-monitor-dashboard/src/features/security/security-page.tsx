"use client";

import { CheckCheck } from "lucide-react";
import Link from "next/link";
import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Button, Field, Notice, PageHeader, Pill, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Pagination, Time } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { useFilters } from "@/hooks/use-filters";
import { usePage } from "@/hooks/use-page";
import { api } from "@/lib/api";
import { filtersToQuery } from "@/lib/filters";
import { humanize } from "@/lib/format";
import { severityTone } from "@/lib/status";
import { SECURITY_EVENT_KINDS, type Paged, type SecurityEventDto } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

const PAGE_SIZE = 50;
const DEFAULT_SEVERITIES = ["INFO", "WARNING", "CRITICAL"];

export function SecurityPage() {
  const { isAdmin } = useAuth();
  const dateRange = useDateRange("last7");
  const { filters, setFilters } = useFilters();
  const [kind, setKind] = useState("");
  const [severity, setSeverity] = useState("");
  const [resolved, setResolved] = useState("");

  const query = useMemo(
    () => ({ ...dateRange.query, ...filtersToQuery(filters, ["device"]), kind, severity, resolved }),
    [dateRange.query, filters, kind, severity, resolved],
  );
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<SecurityEventDto>>("/security-events", { ...query, page, pageSize: PAGE_SIZE }, { refreshInterval: 30_000 });
  const resolve = useAction(async (id: string) => {
    await api.resolveSecurityEvent(id);
    await mutate();
  });

  const severities = useMemo(() => Array.from(new Set([...DEFAULT_SEVERITIES, ...(data?.items.map((e) => e.severity) ?? [])])), [data]);

  return (
    <>
      <PageHeader
        title="Security & Agent Health"
        subtitle="Authentication failures, offline devices, backlog, outdated agents and collector problems. New events appear automatically."
      />
      <FilterBar
        fields={["device"]}
        value={filters}
        onChange={setFilters}
        dateRange={dateRange}
        extra={
          <>
            <div className="w-48">
              <Field label="Kind">
                <select className="field" value={kind} onChange={(e) => setKind(e.target.value)}>
                  <option value="">All kinds</option>
                  {SECURITY_EVENT_KINDS.map((k) => <option key={k} value={k}>{humanize(k)}</option>)}
                </select>
              </Field>
            </div>
            <div className="w-36">
              <Field label="Severity">
                <select className="field" value={severity} onChange={(e) => setSeverity(e.target.value)}>
                  <option value="">Any</option>
                  {severities.map((s) => <option key={s} value={s}>{humanize(s)}</option>)}
                </select>
              </Field>
            </div>
            <div className="w-36">
              <Field label="State">
                <select className="field" value={resolved} onChange={(e) => setResolved(e.target.value)}>
                  <option value="">All</option>
                  <option value="false">Open</option>
                  <option value="true">Resolved</option>
                </select>
              </Field>
            </div>
          </>
        }
      />
      {resolve.error && <div className="mb-3"><Notice tone="danger">{resolve.error}</Notice></div>}
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No security events match" emptyHint="That is good news. Adjust the period or filters to look further back.">
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Severity</th>
                    <th>Kind</th>
                    <th>Device</th>
                    <th>Message</th>
                    <th>Raised</th>
                    <th>State</th>
                    {isAdmin && <th className="no-print">Action</th>}
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((e) => (
                    <tr key={e.id}>
                      <td><StatusPill value={e.severity} tone={severityTone(e.severity)} /></td>
                      <td className="font-medium">{humanize(e.kind)}</td>
                      <td>{e.deviceId ? <Link className="text-brand hover:underline" href={`/devices/${encodeURIComponent(e.deviceId)}?tab=diagnostics`}>{e.deviceId}</Link> : "—"}</td>
                      <td className="max-w-[420px] text-muted">{e.message}</td>
                      <td><Time value={e.raisedAtUtc} /></td>
                      <td>{e.resolvedAtUtc ? <span className="flex items-center gap-2"><Pill tone="ok">Resolved</Pill><Time value={e.resolvedAtUtc} className="text-[12px] text-faint" /></span> : <Pill tone="warn">Open</Pill>}</td>
                      {isAdmin && (
                        <td className="no-print">
                          {!e.resolvedAtUtc && (
                            <Button size="sm" icon={CheckCheck} loading={resolve.pending} onClick={() => void resolve.run(e.id)}>
                              Resolve
                            </Button>
                          )}
                        </td>
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
    </>
  );
}
