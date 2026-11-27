"use client";

import { Upload } from "lucide-react";
import { useMemo, useState } from "react";
import { Button, Card, Field, Notice, PageHeader } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { ConfirmDialog } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import { formatNumber } from "@/lib/format";
import type { AgentPackageDto, AgentRolloutDto, DeviceSummary, DeviceUpdateStateDto, Paged } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";
import { RolloutPanel } from "./rollout-panel";

type Confirm = "rollout" | "rollback" | null;

export function UpdatesPage() {
  const { isSuperAdmin } = useAuth();
  const rollouts = useApi<AgentRolloutDto[]>("/agent-rollouts", undefined, { refreshInterval: 15_000 });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const current = rollouts.data?.find((r) => r.id === selectedId) ?? rollouts.data?.[0];
  const devicesState = useApi<DeviceUpdateStateDto[]>(current ? `/agent-rollouts/${current.id}/devices` : null, undefined, { refreshInterval: 15_000 });

  const [lastUploaded, setLastUploaded] = useState<AgentPackageDto | null>(null);
  const [version, setVersion] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [confirm, setConfirm] = useState<Confirm>(null);

  const upload = useAction(async () => {
    if (!file) return;
    const pkg = await api.uploadAgentPackage(file);
    setLastUploaded(pkg);
    setVersion(pkg.version);
    setFile(null);
  });
  const create = useAction(async () => {
    const created = await api.createRollout(version.trim());
    setSelectedId(created.id);
    await rollouts.mutate();
  });
  const lifecycle = useAction(async (fn: (id: string) => Promise<AgentRolloutDto>) => {
    if (!current) return;
    await fn(current.id);
    setConfirm(null);
    await Promise.all([rollouts.mutate(), devicesState.mutate()]);
  });

  return (
    <>
      <PageHeader
        title="Agent Updates"
        subtitle="Upload a signed agent package, try it on a canary group, then roll it out to every device once the canary is healthy."
      />
      <div className="space-y-4">
        {isSuperAdmin && (
          <Card title="Upload package">
            <div className="flex flex-wrap items-end gap-3">
              <div className="min-w-64">
                <Field label="Package (.zip with manifest.json)">
                  <input className="field" type="file" accept=".zip" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
                </Field>
              </div>
              <Button icon={Upload} disabled={!file} loading={upload.pending} onClick={() => void upload.run()}>Upload</Button>
            </div>
            {upload.error && <div className="mt-3"><Notice tone="danger">{upload.error}</Notice></div>}
            {lastUploaded && (
              <div className="mt-3">
                <Notice tone="ok">Uploaded version {lastUploaded.version} ({formatNumber(Math.round(lastUploaded.sizeBytes / 1024))} KB), signed by {lastUploaded.signerSubject ?? "unknown"}.</Notice>
              </div>
            )}
          </Card>
        )}

        <Card title="New rollout">
          <div className="flex flex-wrap items-end gap-3">
            <div className="w-56">
              <Field label="Uploaded version" hint="Must match an uploaded package.">
                <input className="field" value={version} placeholder="1.5.0.0" onChange={(e) => setVersion(e.target.value)} />
              </Field>
            </div>
            <Button variant="primary" disabled={!version.trim()} loading={create.pending} onClick={() => void create.run()}>Create rollout</Button>
          </div>
          {create.error && <div className="mt-3"><Notice tone="danger">{create.error}</Notice></div>}
        </Card>

        <DataState
          loading={rollouts.loading}
          error={rollouts.error}
          isEmpty={!current}
          onRetry={() => void rollouts.mutate()}
          emptyTitle="No rollouts yet"
          emptyHint="Create a rollout for an uploaded package version to begin."
        >
          {current && (
            <div className="space-y-4">
              {rollouts.data && rollouts.data.length > 1 && (
                <div className="w-64">
                  <Field label="Rollout">
                    <select className="field" value={current.id} onChange={(e) => setSelectedId(e.target.value)}>
                      {rollouts.data.map((r) => <option key={r.id} value={r.id}>{r.version} ({r.state})</option>)}
                    </select>
                  </Field>
                </div>
              )}
              {lifecycle.error && confirm === null && <Notice tone="danger">{lifecycle.error}</Notice>}
              <RolloutPanel
                rollout={current}
                devices={devicesState.data ?? []}
                busy={lifecycle.pending}
                onRollOut={() => setConfirm("rollout")}
                onPause={() => void lifecycle.run(api.pauseRollout)}
                onRollback={() => setConfirm("rollback")}
              />
              {(current.state === "Draft" || current.state === "Canary") && (
                <CanaryPicker key={current.id} rollout={current} onSaved={() => void rollouts.mutate()} />
              )}
            </div>
          )}
        </DataState>
      </div>
      <ConfirmDialog
        open={confirm === "rollout"}
        title="Roll out to all devices"
        message={`Every enrolled device will be offered version ${current?.version ?? ""}.`}
        confirmLabel="Roll out to all"
        loading={lifecycle.pending}
        error={lifecycle.error}
        onConfirm={() => void lifecycle.run(api.promoteRollout)}
        onClose={() => {
          lifecycle.reset();
          setConfirm(null);
        }}
      />
      <ConfirmDialog
        open={confirm === "rollback"}
        title="Roll back this rollout"
        message="No further devices will be offered this version. Devices that already failed stay quarantined from it."
        confirmLabel="Roll back"
        danger
        loading={lifecycle.pending}
        error={lifecycle.error}
        onConfirm={() => void lifecycle.run(api.rollbackRollout)}
        onClose={() => {
          lifecycle.reset();
          setConfirm(null);
        }}
      />
    </>
  );
}

function CanaryPicker({ rollout, onSaved }: { rollout: AgentRolloutDto; onSaved: () => void }) {
  const devices = useApi<Paged<DeviceSummary>>("/devices", { pageSize: 200 });
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const items = useMemo(() => devices.data?.items ?? [], [devices.data]);
  const save = useAction(async () => {
    await api.setRolloutCanary(rollout.id, [...picked]);
    onSaved();
  });
  const toggle = (id: string) =>
    setPicked((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  return (
    <Card
      title="Canary devices"
      actions={
        <Button variant="primary" size="sm" disabled={picked.size === 0} loading={save.pending} onClick={() => void save.run()}>
          Start canary with {picked.size}
        </Button>
      }
    >
      <DataState loading={devices.loading} error={devices.error} isEmpty={items.length === 0} emptyTitle="No devices enrolled">
        <div className="grid gap-1 sm:grid-cols-2 lg:grid-cols-3">
          {items.map((d) => (
            <label key={d.id} className="flex items-center gap-2 text-[13px]">
              <input type="checkbox" checked={picked.has(d.id)} onChange={() => toggle(d.id)} />
              <span className="font-medium">{d.deviceId}</span>
              <span className="text-muted">{d.hostname} - v{d.agentVersion}</span>
            </label>
          ))}
        </div>
      </DataState>
      {save.error && <div className="mt-3"><Notice tone="danger">{save.error}</Notice></div>}
    </Card>
  );
}
