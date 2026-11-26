"use client";

import { KeyRound, Trash2 } from "lucide-react";
import { useState } from "react";
import { CreateTokenDialog } from "@/features/devices/enrollment-dialogs";
import { Button, Card, Notice, Pill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { ConfirmDialog, Time } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import type { EnrollmentTokenDto } from "@/lib/types";

function tokenState(t: EnrollmentTokenDto, now: number): { label: string; tone: "ok" | "neutral" | "warn" } {
  if (t.usedAtUtc) return { label: "Used", tone: "neutral" };
  if (t.expiresAtUtc && Date.parse(t.expiresAtUtc) < now) return { label: "Expired", tone: "warn" };
  return { label: "Active", tone: "ok" };
}

export function TokensPanel() {
  const { data, error, loading, mutate } = useApi<EnrollmentTokenDto[]>("/enrollment-tokens");
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<EnrollmentTokenDto | null>(null);
  const remove = useAction(async (id: string) => {
    await api.deleteEnrollmentToken(id);
    await mutate();
    return true;
  });
  const [now] = useState(() => Date.now());

  return (
    <Card title="Enrollment tokens" padded={false} actions={<Button size="sm" variant="primary" icon={KeyRound} onClick={() => setCreating(true)}>Create token</Button>}>
      <div className="p-4"><Notice tone="info">Token values are shown only once, at creation. This list shows metadata; delete a token to invalidate it before it is used.</Notice></div>
      <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.length === 0} emptyTitle="No enrollment tokens">
        <div className="table-wrap !rounded-none !border-x-0 !border-b-0">
          <table className="data-table">
            <thead><tr><th>State</th><th>Employee</th><th>Bound device</th><th>Note</th><th>Created</th><th>Expires</th><th>Used by</th><th /></tr></thead>
            <tbody>
              {(data ?? []).map((t) => {
                const st = tokenState(t, now);
                return (
                  <tr key={t.id}>
                    <td><Pill tone={st.tone}>{st.label}</Pill></td>
                    <td>{t.employeeCode ?? "—"}</td>
                    <td>{t.deviceId ?? "any"}</td>
                    <td className="text-muted">{t.note ?? "—"}</td>
                    <td><Time value={t.createdAtUtc} /></td>
                    <td>{t.expiresAtUtc ? <Time value={t.expiresAtUtc} /> : "Never"}</td>
                    <td>{t.usedByDeviceId ?? "—"}</td>
                    <td className="text-right">{!t.usedAtUtc && <Button size="sm" variant="ghost" iconOnly icon={Trash2} aria-label="Delete token" onClick={() => setDeleting(t)} />}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </DataState>
      <CreateTokenDialog open={creating} onClose={() => setCreating(false)} onCreated={() => void mutate()} />
      {deleting && (
        <ConfirmDialog open title="Delete enrollment token" message="This invalidates the token so it can no longer be used to enroll a PC." confirmLabel="Delete" danger loading={remove.pending} error={remove.error}
          onClose={() => { remove.reset(); setDeleting(null); }}
          onConfirm={() => void remove.run(deleting.id).then((ok) => { if (ok) setDeleting(null); })} />
      )}
    </Card>
  );
}
