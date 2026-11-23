"use client";

import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Field, Notice, PageHeader } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Pagination, Time } from "@/components/ui/widgets";
import { useApi } from "@/hooks/use-api";
import { useDateRange } from "@/hooks/use-date-range";
import { usePage } from "@/hooks/use-page";
import type { AuditLogDto, Paged } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

const PAGE_SIZE = 100;

export function AuditPage() {
  const { isAdmin } = useAuth();
  const dateRange = useDateRange("last7");
  const [user, setUser] = useState("");
  const [action, setAction] = useState("");
  const query = useMemo(() => ({ ...dateRange.query, user, action }), [dateRange.query, user, action]);
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<AuditLogDto>>("/audit-logs", { ...query, page, pageSize: PAGE_SIZE }, { enabled: isAdmin });

  if (!isAdmin) {
    return (
      <>
        <PageHeader title="Audit Log" />
        <Notice tone="warn">The audit log is available to administrators only.</Notice>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Audit Log" subtitle="Every administrative action: sign-ins, device actions, settings changes and exports." />
      <FilterBar
        fields={[]}
        value={{}}
        onChange={() => undefined}
        dateRange={dateRange}
        extra={
          <>
            <div className="w-40"><Field label="User"><input className="field" value={user} onChange={(e) => setUser(e.target.value)} placeholder="username" /></Field></div>
            <div className="w-48"><Field label="Action"><input className="field" value={action} onChange={(e) => setAction(e.target.value)} placeholder="e.g. device.revoke" /></Field></div>
          </>
        }
      />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No audit entries match">
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead><tr><th>Time</th><th>User</th><th>Action</th><th>Target</th><th>Detail</th><th>IP address</th></tr></thead>
                <tbody>
                  {data.items.map((a) => (
                    <tr key={a.id}>
                      <td className="whitespace-nowrap"><Time value={a.timestampUtc} /></td>
                      <td>{a.username ?? "system"}</td>
                      <td className="font-medium">{a.action}</td>
                      <td>{a.target ?? "—"}</td>
                      <td className="max-w-[360px] text-muted">{a.detail ?? "—"}</td>
                      <td className="mono">{a.ipAddress ?? "—"}</td>
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
