"use client";

import { Ban, Play, RotateCcw, ShieldOff } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/primitives";
import { ConfirmDialog } from "@/components/ui/widgets";
import { api } from "@/lib/api";
import { useAction } from "@/hooks/use-action";
import { useAuth } from "@/providers/auth-provider";
import type { EnrollmentTokenDto } from "@/lib/types";
import { TokenRevealDialog } from "./enrollment-dialogs";

type Pending = "disable" | "enable" | "revoke" | "re-enroll" | null;

const COPY: Record<Exclude<Pending, null>, { title: string; message: string; label: string; danger: boolean }> = {
  disable: {
    title: "Disable device",
    message: "The agent will stop collecting and uploading (server answers 403) until the device is enabled again.",
    label: "Disable",
    danger: false,
  },
  enable: { title: "Enable device", message: "The agent resumes collecting and uploading on its next heartbeat.", label: "Enable", danger: false },
  revoke: {
    title: "Revoke device",
    message: "Revoking invalidates the device credentials permanently. The PC must be re-enrolled with a new token to report again.",
    label: "Revoke",
    danger: true,
  },
  "re-enroll": {
    title: "Re-enroll device",
    message: "Creates a one-time enrollment token bound to this device. The token is shown once.",
    label: "Create token",
    danger: false,
  },
};

interface Props {
  deviceId: string;
  status: string;
  onChanged?: () => void;
  size?: "sm" | "md";
}

/** ADMIN+ device actions: disable / enable / revoke / re-enroll. Renders nothing for VIEWER. */
export function DeviceActions({ deviceId, status, onChanged, size = "sm" }: Props) {
  const { isAdmin } = useAuth();
  const [pending, setPending] = useState<Pending>(null);
  const [token, setToken] = useState<EnrollmentTokenDto | null>(null);

  const run = useAction(async (kind: Exclude<Pending, null>) => {
    switch (kind) {
      case "disable":
        await api.disableDevice(deviceId);
        return null;
      case "enable":
        await api.enableDevice(deviceId);
        return null;
      case "revoke":
        await api.revokeDevice(deviceId);
        return null;
      case "re-enroll":
        return api.reEnrollDevice(deviceId);
    }
  });

  if (!isAdmin) return null;
  const revoked = status === "REVOKED";
  const disabled = status === "DISABLED";

  async function confirm() {
    if (!pending) return;
    const result = await run.run(pending);
    if (result === undefined) return; // error shown in dialog
    const kind = pending;
    setPending(null);
    if (kind === "re-enroll" && result) setToken(result);
    onChanged?.();
  }

  return (
    <>
      <div className={size === "sm" ? "flex flex-nowrap gap-1.5" : "flex flex-wrap gap-1.5"}>
        {disabled ? (
          <Button size={size} icon={Play} onClick={() => setPending("enable")}>Enable</Button>
        ) : (
          !revoked && <Button size={size} icon={Ban} onClick={() => setPending("disable")}>Disable</Button>
        )}
        {!revoked && (
          <Button size={size} icon={ShieldOff} onClick={() => setPending("revoke")}>Revoke</Button>
        )}
        <Button size={size} icon={RotateCcw} onClick={() => setPending("re-enroll")}>Re-enroll</Button>
      </div>
      {pending && (
        <ConfirmDialog
          open
          title={`${COPY[pending].title}: ${deviceId}`}
          message={COPY[pending].message}
          confirmLabel={COPY[pending].label}
          danger={COPY[pending].danger}
          loading={run.pending}
          error={run.error}
          onConfirm={() => void confirm()}
          onClose={() => {
            run.reset();
            setPending(null);
          }}
        />
      )}
      <TokenRevealDialog token={token} title={`Re-enrollment token: ${deviceId}`} onClose={() => setToken(null)} />
    </>
  );
}
