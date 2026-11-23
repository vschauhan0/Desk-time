"use client";

import { Pencil, Plus, Trash2 } from "lucide-react";
import { useState } from "react";
import { Button, Card, Field, Notice, Pill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { ConfirmDialog, Dialog, Time } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import type { AdminUserDto, AdminUserUpsert, Role } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

const ROLES: Role[] = ["VIEWER", "ADMIN", "SUPERADMIN"];

function UserDialog({ user, onClose, onSaved }: { user: AdminUserDto | null; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState<AdminUserUpsert>(() => ({
    username: user?.username ?? "",
    displayName: user?.displayName ?? "",
    role: user?.role ?? "VIEWER",
    enabled: user?.enabled ?? true,
    password: null,
  }));
  const save = useAction(async (body: AdminUserUpsert) => (user ? api.updateAdminUser(user.id, body) : api.createAdminUser(body)));
  const needsPassword = !user;
  const valid = form.username.trim() && form.displayName.trim() && (!needsPassword || (form.password ?? "").length >= 8);

  async function submit() {
    const res = await save.run({ ...form, password: form.password ? form.password : null });
    if (res) {
      onSaved();
      onClose();
    }
  }

  return (
    <Dialog open onClose={onClose} title={user ? `Edit ${user.username}` : "Add administrator"} footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" loading={save.pending} disabled={!valid} onClick={() => void submit()}>Save</Button></>}>
      <div className="space-y-3">
        {save.error && <Notice tone="danger">{save.error}</Notice>}
        <Field label="Username"><input className="field" value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} autoComplete="off" /></Field>
        <Field label="Display name"><input className="field" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} /></Field>
        <Field label="Role" hint="Viewer: read-only. Admin: device actions, settings, exports. Superadmin: also manages administrators.">
          <select className="field" value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as Role })}>
            {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </Field>
        <Field label={needsPassword ? "Password (min 8 characters)" : "New password (leave empty to keep)"}>
          <input className="field" type="password" value={form.password ?? ""} onChange={(e) => setForm({ ...form, password: e.target.value })} autoComplete="new-password" />
        </Field>
        <label className="flex items-center gap-2 text-[13px]"><input type="checkbox" checked={form.enabled} onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Account enabled</label>
      </div>
    </Dialog>
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
            <thead><tr><th>Username</th><th>Name</th><th>Role</th><th>State</th><th>Created</th><th>Last login</th><th /></tr></thead>
            <tbody>
              {(data ?? []).map((u) => (
                <tr key={u.id}>
                  <td className="font-medium">{u.username}{u.id === me?.id && <span className="ml-2 text-[11px] text-faint">(you)</span>}</td>
                  <td>{u.displayName}</td>
                  <td><Pill tone={u.role === "SUPERADMIN" ? "brand" : u.role === "ADMIN" ? "info" : "neutral"} icon={false}>{u.role}</Pill></td>
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
