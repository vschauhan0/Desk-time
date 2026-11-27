"use client";

import { Pencil, Plus, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import { Button, Card, Field, Notice, Pill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { ConfirmDialog, Dialog, Time } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import { roleAtLeast } from "@/lib/roles";
import type { AdminUserDto, AdminUserUpsert, AppPageInfo, EmployeeDto, PageAccessMap, PageLevel, Paged, Role } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

const ROLES: Role[] = ["VIEWER", "ADMIN", "SUPERADMIN"];

/** A checkbox list grouped under a heading, used for both page access and employee scope. */
function CheckList({ children }: { children: React.ReactNode }) {
  return <div className="max-h-44 space-y-1.5 overflow-auto rounded-lg border border-line p-2.5">{children}</div>;
}

function CheckRow({ checked, onChange, label, sub }: { checked: boolean; onChange: (v: boolean) => void; label: string; sub?: string }) {
  return (
    <label className="flex cursor-pointer items-center gap-2 text-[13px]">
      <input type="checkbox" className="size-4 accent-[var(--brand)]" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className="min-w-0 truncate">{label}{sub && <span className="text-faint"> · {sub}</span>}</span>
    </label>
  );
}

function toggle(list: string[] | null | undefined, value: string, on: boolean): string[] {
  const set = new Set(list ?? []);
  if (on) set.add(value);
  else set.delete(value);
  return [...set];
}

/** One page/sub-page row with a Hidden/View/Edit selector. Non-editable pages offer Hidden/View only. */
function PageLevelRow({ page, map, onSet, indent }: { page: AppPageInfo; map: PageAccessMap; onSet: (key: string, level: PageLevel) => void; indent?: boolean }) {
  // Effective level: explicit entry, else inherit the parent's, else Hidden.
  const explicit = map[page.key];
  const inherited = page.parent ? map[page.parent] : undefined;
  const effective: PageLevel = explicit ?? inherited ?? "HIDDEN";
  const levels: PageLevel[] = page.editable ? ["HIDDEN", "VIEW", "EDIT"] : ["HIDDEN", "VIEW"];
  return (
    <div className={`flex items-center justify-between gap-3 rounded px-1.5 py-1 hover:bg-surface-2 ${indent ? "pl-6" : ""}`}>
      <span className="min-w-0 truncate text-[13px]">{page.label}{!explicit && inherited && <span className="ml-1 text-[11px] text-faint">(inherits {inherited.toLowerCase()})</span>}</span>
      <select
        className="field !h-7 !w-28 shrink-0 !py-0 text-[12px]"
        value={effective}
        onChange={(e) => onSet(page.key, e.target.value as PageLevel)}
      >
        {levels.map((l) => <option key={l} value={l}>{l === "HIDDEN" ? "Hidden" : l === "VIEW" ? "View" : "Edit"}</option>)}
      </select>
    </div>
  );
}

function UserDialog({ user, onClose, onSaved }: { user: AdminUserDto | null; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState<AdminUserUpsert>(() => ({
    username: user?.username ?? "",
    displayName: user?.displayName ?? "",
    role: user?.role ?? "VIEWER",
    enabled: user?.enabled ?? true,
    password: null,
    pageAccess: user?.pageAccess ?? null,
    scopeAllEmployees: user?.scopeAllEmployees ?? true,
    allowedDepartments: user?.allowedDepartments ?? [],
    allowedEmployeeIds: user?.allowedEmployeeIds ?? [],
  }));
  const set = (patch: Partial<AdminUserUpsert>) => setForm((f) => ({ ...f, ...patch }));

  // Reference data for the pickers (only superadmins reach this panel, so these are always permitted).
  const pages = useApi<AppPageInfo[]>("/admin-users/pages");
  const depts = useApi<string[]>("/departments");
  const emps = useApi<Paged<EmployeeDto>>("/employees", { pageSize: 500 });

  const save = useAction(async (body: AdminUserUpsert) => (user ? api.updateAdminUser(user.id, body) : api.createAdminUser(body)));
  const needsPassword = !user;
  const valid = form.username.trim() && form.displayName.trim() && (!needsPassword || (form.password ?? "").length >= 8);
  const isSuper = form.role === "SUPERADMIN";

  // Pages this role can reach at all; page restrictions only make sense within that set.
  const rolePages = useMemo(() => (pages.data ?? []).filter((p) => roleAtLeast(form.role, p.minRole)), [pages.data, form.role]);

  const restrictPages = form.pageAccess != null;
  const pageMap = form.pageAccess ?? {};
  const setLevel = (key: string, level: PageLevel) => {
    const next = { ...(form.pageAccess ?? {}) };
    if (level === "HIDDEN") delete next[key];
    else next[key] = level;
    set({ pageAccess: next });
  };

  async function submit() {
    const body: AdminUserUpsert = {
      ...form,
      password: form.password ? form.password : null,
      // Superadmins are always unrestricted; send clean nulls so the server stores nothing.
      pageAccess: isSuper ? null : form.pageAccess,
      scopeAllEmployees: isSuper ? true : form.scopeAllEmployees,
      allowedDepartments: isSuper ? [] : form.allowedDepartments,
      allowedEmployeeIds: isSuper ? [] : form.allowedEmployeeIds,
    };
    const res = await save.run(body);
    if (res) {
      onSaved();
      onClose();
    }
  }

  return (
    <Dialog open onClose={onClose} width={620} title={user ? `Edit ${user.username}` : "Add administrator"} footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" loading={save.pending} disabled={!valid} onClick={() => void submit()}>Save</Button></>}>
      <div className="space-y-3">
        {save.error && <Notice tone="danger">{save.error}</Notice>}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Username"><input className="field" value={form.username} onChange={(e) => set({ username: e.target.value })} autoComplete="off" /></Field>
          <Field label="Display name"><input className="field" value={form.displayName} onChange={(e) => set({ displayName: e.target.value })} /></Field>
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Role" hint="Viewer: read-only. Admin: device actions, settings. Superadmin: also manages admins & RBAC.">
            <select className="field" value={form.role} onChange={(e) => set({ role: e.target.value as Role })}>
              {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
            </select>
          </Field>
          <Field label={needsPassword ? "Password (min 8 characters)" : "New password (leave empty to keep)"}>
            <input className="field" type="password" value={form.password ?? ""} onChange={(e) => set({ password: e.target.value })} autoComplete="new-password" />
          </Field>
        </div>
        <label className="flex items-center gap-2 text-[13px]"><input type="checkbox" checked={form.enabled} onChange={(e) => set({ enabled: e.target.checked })} /> Account enabled</label>

        {isSuper ? (
          <Notice tone="info">Superadmins can open every page and see every employee. Page and employee limits apply to Admin and Viewer accounts.</Notice>
        ) : (
          <>
            {/* ----- Page access ----- */}
            <div className="border-t border-line pt-3">
              <p className="mb-1.5 text-[12px] font-semibold uppercase tracking-wide text-muted">Page access</p>
              <label className="flex items-center gap-2 text-[13px]"><input type="radio" checked={!restrictPages} onChange={() => set({ pageAccess: null })} /> Full access to every page this role allows</label>
              <label className="mt-1 flex items-center gap-2 text-[13px]"><input type="radio" checked={restrictPages} onChange={() => set({ pageAccess: {} })} /> Set access per page</label>
              {restrictPages && (
                <div className="mt-2">
                  <DataState loading={pages.loading} error={pages.error} onRetry={() => void pages.mutate()}>
                    <div className="max-h-72 space-y-0.5 overflow-auto rounded-lg border border-line p-2.5">
                      <p className="mb-1 text-[11px] text-faint">Hidden = not shown · View = read-only · Edit = can make changes. Sub-tabs follow their page unless set.</p>
                      {rolePages.filter((p) => p.parent == null).map((p) => {
                        const subs = rolePages.filter((s) => s.parent === p.key);
                        return (
                          <div key={p.key}>
                            <PageLevelRow page={p} map={pageMap} onSet={setLevel} />
                            {subs.map((s) => <PageLevelRow key={s.key} page={s} map={pageMap} onSet={setLevel} indent />)}
                          </div>
                        );
                      })}
                    </div>
                  </DataState>
                </div>
              )}
            </div>

            {/* ----- Employee access ----- */}
            <div className="border-t border-line pt-3">
              <p className="mb-1.5 text-[12px] font-semibold uppercase tracking-wide text-muted">Employee access</p>
              <label className="flex items-center gap-2 text-[13px]"><input type="radio" checked={form.scopeAllEmployees} onChange={() => set({ scopeAllEmployees: true })} /> All employees</label>
              <label className="mt-1 flex items-center gap-2 text-[13px]"><input type="radio" checked={!form.scopeAllEmployees} onChange={() => set({ scopeAllEmployees: false })} /> Only the departments / employees I choose</label>
              {!form.scopeAllEmployees && (
                <div className="mt-2 grid gap-3 sm:grid-cols-2">
                  <div>
                    <p className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-faint">Departments</p>
                    <DataState loading={depts.loading} error={depts.error} onRetry={() => void depts.mutate()}>
                      <CheckList>
                        {(depts.data ?? []).length === 0 && <p className="text-[12px] text-muted">No departments.</p>}
                        {(depts.data ?? []).map((d) => (
                          <CheckRow key={d} label={d} checked={(form.allowedDepartments ?? []).includes(d)}
                            onChange={(on) => set({ allowedDepartments: toggle(form.allowedDepartments, d, on) })} />
                        ))}
                      </CheckList>
                    </DataState>
                  </div>
                  <div>
                    <p className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-faint">Individual employees</p>
                    <DataState loading={emps.loading} error={emps.error} onRetry={() => void emps.mutate()}>
                      <CheckList>
                        {(emps.data?.items ?? []).map((e) => (
                          <CheckRow key={e.id} label={e.fullName} sub={e.employeeCode} checked={(form.allowedEmployeeIds ?? []).includes(e.id)}
                            onChange={(on) => set({ allowedEmployeeIds: toggle(form.allowedEmployeeIds, e.id, on) })} />
                        ))}
                      </CheckList>
                    </DataState>
                  </div>
                </div>
              )}
            </div>
          </>
        )}
      </div>
    </Dialog>
  );
}

/** At-a-glance access summary for the table: pages and employees a user is limited to. */
function AccessCell({ user }: { user: AdminUserDto }) {
  if (user.role === "SUPERADMIN") return <span className="text-[12px] text-faint">Full access</span>;
  const grantedKeys = user.pageAccess ? Object.keys(user.pageAccess).filter((k) => !k.includes(".")) : null;
  const pageBit = grantedKeys == null ? "All pages" : `${grantedKeys.length} page${grantedKeys.length === 1 ? "" : "s"}`;
  const empBit = user.scopeAllEmployees
    ? "all employees"
    : `${user.allowedDepartments.length} dept${user.allowedDepartments.length === 1 ? "" : "s"} · ${user.allowedEmployeeIds.length} emp`;
  const restricted = user.pageAccess != null || !user.scopeAllEmployees;
  return (
    <span className="inline-flex items-center gap-1.5 text-[12px]">
      {restricted && <Pill tone="warn" icon={false}>Restricted</Pill>}
      <span className="text-muted">{pageBit} · {empBit}</span>
    </span>
  );
}

/** SUPERADMIN only. */
export function AdminUsersPanel() {
  const { user: me } = useAuth();
  const { data, error, loading, mutate } = useApi<AdminUserDto[]>("/admin-users");
  const [editing, setEditing] = useState<{ user: AdminUserDto | null } | null>(null);
  const [deleting, setDeleting] = useState<AdminUserDto | null>(null);
  const remove = useAction(async (id: string) => {
    await api.deleteAdminUser(id);
    await mutate();
    return true;
  });

  return (
    <Card title="Administrator accounts" padded={false} actions={<Button size="sm" variant="primary" icon={Plus} onClick={() => setEditing({ user: null })}>Add administrator</Button>}>
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.length === 0} emptyTitle="No administrator accounts">
        <div className="table-wrap !rounded-none !border-x-0 !border-b-0">
          <table className="data-table">
            <thead><tr><th>Username</th><th>Name</th><th>Role</th><th>Access</th><th>State</th><th>Created</th><th>Last login</th><th /></tr></thead>
            <tbody>
              {(data ?? []).map((u) => (
                <tr key={u.id}>
                  <td className="font-medium">{u.username}{u.id === me?.id && <span className="ml-2 text-[11px] text-faint">(you)</span>}</td>
                  <td>{u.displayName}</td>
                  <td><Pill tone={u.role === "SUPERADMIN" ? "brand" : u.role === "ADMIN" ? "info" : "neutral"} icon={false}>{u.role}</Pill></td>
                  <td><AccessCell user={u} /></td>
                  <td>{u.enabled ? <Pill tone="ok">Enabled</Pill> : <Pill tone="neutral">Disabled</Pill>}</td>
                  <td><Time value={u.createdAtUtc} /></td>
                  <td><Time value={u.lastLoginUtc} /></td>
                  <td>
                    <div className="flex justify-end gap-1">
                      <Button size="sm" variant="ghost" iconOnly icon={Pencil} aria-label={`Edit ${u.username}`} onClick={() => setEditing({ user: u })} />
                      {u.id !== me?.id && <Button size="sm" variant="ghost" iconOnly icon={Trash2} aria-label={`Delete ${u.username}`} onClick={() => setDeleting(u)} />}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </DataState>
      {editing && <UserDialog key={editing.user?.id ?? "new"} user={editing.user} onClose={() => setEditing(null)} onSaved={() => void mutate()} />}
      {deleting && (
        <ConfirmDialog open title={`Delete ${deleting.username}`} message="This administrator will no longer be able to sign in. The action is recorded in the audit log." confirmLabel="Delete" danger loading={remove.pending} error={remove.error}
          onClose={() => { remove.reset(); setDeleting(null); }}
          onConfirm={() => void remove.run(deleting.id).then((ok) => { if (ok) setDeleting(null); })} />
      )}
    </Card>
  );
}
