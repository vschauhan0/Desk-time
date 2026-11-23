"use client";

import { Pencil, Plus } from "lucide-react";
import Link from "next/link";
import { useMemo, useState } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Button, Field, Notice, PageHeader, Pill, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Dialog, Pagination, RelativeTime } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { useFilters } from "@/hooks/use-filters";
import { usePage } from "@/hooks/use-page";
import { api } from "@/lib/api";
import { filtersToQuery } from "@/lib/filters";
import { deviceStatusTone, presenceTone } from "@/lib/status";
import type { EmployeeDto, EmployeeUpsert, Paged } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

const PAGE_SIZE = 50;

function EmployeeDialog({ employee, open, onClose, onSaved }: { employee: EmployeeDto | null; open: boolean; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState<EmployeeUpsert>(() => ({
    employeeCode: employee?.employeeCode ?? "",
    fullName: employee?.fullName ?? "",
    department: employee?.department ?? "",
    email: employee?.email ?? "",
    active: employee?.active ?? true,
  }));
  const save = useAction(async (body: EmployeeUpsert) => (employee ? api.updateEmployee(employee.id, body) : api.createEmployee(body)));

  async function submit() {
    const result = await save.run({ ...form, department: form.department?.trim() || null, email: form.email?.trim() || null });
    if (result) {
      onSaved();
      onClose();
    }
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={employee ? `Edit ${employee.fullName}` : "Add employee"}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={save.pending} disabled={!form.employeeCode.trim() || !form.fullName.trim()} onClick={() => void submit()}>
            Save
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {save.error && <Notice tone="danger">{save.error}</Notice>}
        <Field label="Employee code"><input className="field" value={form.employeeCode} onChange={(e) => setForm({ ...form, employeeCode: e.target.value })} /></Field>
        <Field label="Full name"><input className="field" value={form.fullName} onChange={(e) => setForm({ ...form, fullName: e.target.value })} /></Field>
        <Field label="Department"><input className="field" value={form.department ?? ""} onChange={(e) => setForm({ ...form, department: e.target.value })} /></Field>
        <Field label="Email"><input className="field" type="email" value={form.email ?? ""} onChange={(e) => setForm({ ...form, email: e.target.value })} /></Field>
        <label className="flex items-center gap-2 text-[13px]">
          <input type="checkbox" checked={form.active} onChange={(e) => setForm({ ...form, active: e.target.checked })} /> Active employee
        </label>
      </div>
    </Dialog>
  );
}

export function EmployeesPage() {
  const { isAdmin } = useAuth();
  const { filters, setFilters } = useFilters();
  const query = useMemo(() => filtersToQuery(filters, ["department", "search"]), [filters]);
  const [page, setPage] = usePage(JSON.stringify(query));
  const { data, error, loading, mutate } = useApi<Paged<EmployeeDto>>("/employees", { ...query, page, pageSize: PAGE_SIZE }, { refreshInterval: 30_000 });
  const [editing, setEditing] = useState<{ employee: EmployeeDto | null } | null>(null);

  return (
    <>
      <PageHeader
        title="Employees"
        subtitle="Presence and status are those of each employee's most recently seen device."
        actions={isAdmin && <Button variant="primary" icon={Plus} onClick={() => setEditing({ employee: null })}>Add employee</Button>}
      />
      <FilterBar fields={["department", "search"]} value={filters} onChange={setFilters} searchPlaceholder="Name, code or email..." />
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.items.length === 0} emptyTitle="No employees found" emptyHint="Employees are created by an administrator or assigned when a device is enrolled with an employee code.">
        {data && (
          <>
            <div className="table-wrap">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Employee</th>
                    <th>Code</th>
                    <th>Department</th>
                    <th className="num">Devices</th>
                    <th>Device status</th>
                    <th>Presence</th>
                    <th>Last seen</th>
                    {isAdmin && <th className="no-print" />}
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((e) => (
                    <tr key={e.id}>
                      <td>
                        <Link href={`/employees/${encodeURIComponent(e.id)}`} className="font-medium text-brand hover:underline">{e.fullName}</Link>
                        {!e.active && <span className="ml-2"><Pill tone="neutral" icon={false}>Inactive</Pill></span>}
                        {e.email && <p className="text-[12px] text-muted">{e.email}</p>}
                      </td>
                      <td className="mono">{e.employeeCode}</td>
                      <td>{e.department ?? "—"}</td>
                      <td className="num">{e.deviceCount}</td>
                      <td>{e.status ? <StatusPill value={e.status} tone={deviceStatusTone(e.status)} /> : "—"}</td>
                      <td>{e.presence ? <StatusPill value={e.presence} tone={presenceTone(e.presence)} /> : "—"}</td>
                      <td><RelativeTime value={e.lastSeenUtc} /></td>
                      {isAdmin && (
                        <td className="no-print text-right">
                          <Button size="sm" variant="ghost" iconOnly icon={Pencil} aria-label={`Edit ${e.fullName}`} onClick={() => setEditing({ employee: e })} />
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
      {editing && <EmployeeDialog key={editing.employee?.id ?? "new"} employee={editing.employee} open onClose={() => setEditing(null)} onSaved={() => void mutate()} />}
    </>
  );
}
