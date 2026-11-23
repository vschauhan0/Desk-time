"use client";

import { KeyRound } from "lucide-react";
import { useState } from "react";
import { Button, Field, Notice } from "@/components/ui/primitives";
import { CopyField, Dialog } from "@/components/ui/widgets";
import { api } from "@/lib/api";
import { useAction } from "@/hooks/use-action";
import { formatDateTime } from "@/lib/format";
import type { EnrollmentTokenDto } from "@/lib/types";

/** Shows a freshly issued enrollment token exactly once, in a copyable field. */
export function TokenRevealDialog({ token, title = "Enrollment token", onClose }: { token: EnrollmentTokenDto | null; title?: string; onClose: () => void }) {
  return (
    <Dialog open={token !== null} onClose={onClose} title={title} footer={<Button variant="primary" onClick={onClose}>Done</Button>}>
      {token && (
        <div className="space-y-4">
          <Notice tone="warn">
            Copy this token now. It is shown <strong>only once</strong> and cannot be retrieved later. It expires {formatDateTime(token.expiresAtUtc)}.
          </Notice>
          <CopyField label="One-time enrollment token" value={token.token ?? "(no token returned)"} />
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-[12.5px]">
            <dt className="text-muted">Bound device</dt>
            <dd>{token.deviceId ?? "any device"}</dd>
            <dt className="text-muted">Employee</dt>
            <dd>{token.employeeCode ?? "not pre-assigned"}</dd>
            {token.note && (
              <>
                <dt className="text-muted">Note</dt>
                <dd>{token.note}</dd>
              </>
            )}
          </dl>
          <p className="text-[12px] text-faint">Enter the token during agent installation (setup wizard or the ENROLLMENT_TOKEN installer property).</p>
        </div>
      )}
    </Dialog>
  );
}

/** Create a new enrollment token (ADMIN+). Reveals the token on success. */
export function CreateTokenDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated?: () => void }) {
  const [employeeCode, setEmployeeCode] = useState("");
  const [deviceId, setDeviceId] = useState("");
  const [hours, setHours] = useState("24");
  const [note, setNote] = useState("");
  const [created, setCreated] = useState<EnrollmentTokenDto | null>(null);
  const create = useAction(api.createEnrollmentToken);

  async function submit() {
    const result = await create.run({
      employeeCode: employeeCode.trim() || null,
      deviceId: deviceId.trim() || null,
      expiresInHours: Math.max(1, Number.parseInt(hours, 10) || 24),
      note: note.trim() || null,
    });
    if (result) {
      setCreated(result);
      onCreated?.();
    }
  }

  function close() {
    setCreated(null);
    create.reset();
    onClose();
  }

  if (created) return <TokenRevealDialog token={created} onClose={close} title="Enrollment token created" />;

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Create enrollment token"
      footer={
        <>
          <Button onClick={close}>Cancel</Button>
          <Button variant="primary" icon={KeyRound} loading={create.pending} onClick={() => void submit()}>
            Create token
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <p className="text-[13px] text-muted">A one-time token lets an agent enroll a PC. Leave the optional fields empty for a generic token.</p>
        {create.error && <Notice tone="danger">{create.error}</Notice>}
        <Field label="Employee code (optional)">
          <input className="field" value={employeeCode} onChange={(e) => setEmployeeCode(e.target.value)} placeholder="EMP1024" />
        </Field>
        <Field label="Bind to device id (optional)" hint="Only that device can use the token (re-enrollment).">
          <input className="field" value={deviceId} onChange={(e) => setDeviceId(e.target.value)} placeholder="PC-024" />
        </Field>
        <Field label="Expires in (hours)">
          <input className="field" type="number" min={1} max={720} value={hours} onChange={(e) => setHours(e.target.value)} />
        </Field>
        <Field label="Note (optional)">
          <input className="field" value={note} onChange={(e) => setNote(e.target.value)} maxLength={200} />
        </Field>
      </div>
    </Dialog>
  );
}
