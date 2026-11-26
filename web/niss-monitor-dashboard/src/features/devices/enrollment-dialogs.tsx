"use client";

import { KeyRound } from "lucide-react";
import { useEffect, useState } from "react";
import { Button, Field, Notice } from "@/components/ui/primitives";
import { CopyField, Dialog } from "@/components/ui/widgets";
import { api } from "@/lib/api";
import { useAction } from "@/hooks/use-action";
import { formatDateTime } from "@/lib/format";
import type { EmployeeDto, EnrollmentTokenDto } from "@/lib/types";

/** Shows a freshly issued enrollment code exactly once, in a copyable field. */
export function TokenRevealDialog({ token, title = "Enrollment code", onClose }: { token: EnrollmentTokenDto | null; title?: string; onClose: () => void }) {
  return (
    <Dialog open={token !== null} onClose={onClose} title={title} footer={<Button variant="primary" onClick={onClose}>Done</Button>}>
      {token && (
        <div className="space-y-4">
          <Notice tone="warn">
            Copy this code now. It is shown <strong>only once</strong> and cannot be retrieved later.{" "}
            {token.expiresAtUtc ? <>It expires {formatDateTime(token.expiresAtUtc)}.</> : <>It <strong>never expires</strong>.</>}
          </Notice>
          <CopyField label="Enrollment code" value={token.token ?? "(no code returned)"} />
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-[12.5px]">
            <dt className="text-muted">Employee</dt>
            <dd>{token.employeeCode ?? "not pre-assigned"}</dd>
            <dt className="text-muted">Bound device</dt>
            <dd>{token.deviceId ?? "any device"}</dd>
            {token.note && (
              <>
                <dt className="text-muted">Note</dt>
                <dd>{token.note}</dd>
              </>
            )}
          </dl>
          <p className="text-[12px] text-faint">The employee enters this code when they run the installer (or pass it as <span className="mono">--token</span>).</p>
        </div>
      )}
    </Dialog>
  );
}

/** Create a new enrollment code (ADMIN+), linked to an employee. Reveals the code on success. */
export function CreateTokenDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated?: () => void }) {
  const [employeeCode, setEmployeeCode] = useState("");
  const [deviceId, setDeviceId] = useState("");
  const [hours, setHours] = useState("24");
  const [neverExpires, setNeverExpires] = useState(false);
  const [note, setNote] = useState("");
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [created, setCreated] = useState<EnrollmentTokenDto | null>(null);
  const create = useAction(api.createEnrollmentToken);

  useEffect(() => {
    if (!open) return;
    void api.employees({ pageSize: 500 }).then((p) => setEmployees(p.items)).catch(() => undefined);
  }, [open]);

  async function submit() {
    const result = await create.run({
      employeeCode: employeeCode.trim() || null,
      deviceId: deviceId.trim() || null,
      expiresInHours: neverExpires ? 0 : Math.max(1, Number.parseInt(hours, 10) || 24),
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

  if (created) return <TokenRevealDialog token={created} onClose={close} title="Enrollment code created" />;

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Create enrollment code"
      footer={
        <>
          <Button onClick={close}>Cancel</Button>
          <Button variant="primary" icon={KeyRound} loading={create.pending} onClick={() => void submit()}>
            Create code
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <p className="text-[13px] text-muted">
          A code lets an employee enroll their PC. Pick the employee to link the PC to their name; turn on “Never expires” so the code works whenever they install.
        </p>
        {create.error && <Notice tone="danger">{create.error}</Notice>}
        <Field label="Employee" hint="The PC that enrolls with this code is assigned to this employee.">
          <select className="field" value={employeeCode} onChange={(e) => setEmployeeCode(e.target.value)}>
            <option value="">— not pre-assigned —</option>
            {employees.map((emp) => (
              <option key={emp.id} value={emp.employeeCode}>
                {emp.fullName} ({emp.employeeCode})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Bind to device id (optional)" hint="Only that device can use the code (re-enrollment).">
          <input className="field" value={deviceId} onChange={(e) => setDeviceId(e.target.value)} placeholder="PC-024" />
        </Field>
        <Field label="Expiry">
          <label className="flex items-center gap-2 text-[13px]">
            <input type="checkbox" checked={neverExpires} onChange={(e) => setNeverExpires(e.target.checked)} />
            Never expires
          </label>
        </Field>
        {!neverExpires && (
          <Field label="Expires in (hours)">
            <input className="field" type="number" min={1} max={720} value={hours} onChange={(e) => setHours(e.target.value)} />
          </Field>
        )}
        <Field label="Note (optional)">
          <input className="field" value={note} onChange={(e) => setNote(e.target.value)} maxLength={200} />
        </Field>
      </div>
    </Dialog>
  );
}
