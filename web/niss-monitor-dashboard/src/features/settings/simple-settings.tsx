"use client";

import { Save } from "lucide-react";
import { useState } from "react";
import { Button, Card, Field, Notice } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import type { HealthThresholds, RetentionSettings } from "@/lib/types";
import { NumberField } from "./form-fields";

function RetentionForm({ initial, onSaved }: { initial: RetentionSettings; onSaved: () => void }) {
  const [v, setV] = useState(initial);
  const [saved, setSaved] = useState(false);
  const save = useAction(api.saveRetention);
  const set = (patch: Partial<RetentionSettings>) => { setSaved(false); setV((s) => ({ ...s, ...patch })); };
  return (
    <Card title="Data retention">
      <div className="space-y-4">
        <p className="text-[13px] text-muted">How long data is kept before the nightly cleanup deletes it. Use 0 to keep forever.</p>
        {save.error && <Notice tone="danger">{save.error}</Notice>}
        {saved && <Notice tone="ok">Retention saved.</Notice>}
        <div className="grid gap-4 sm:grid-cols-2">
          <NumberField label="Raw events" unit="days" value={v.rawEventsDays} onChange={(n) => set({ rawEventsDays: n })} />
          <NumberField label="Application summaries" unit="days" value={v.applicationSummariesDays} onChange={(n) => set({ applicationSummariesDays: n })} />
          <NumberField label="Device health / metrics" unit="days" value={v.deviceHealthDays} onChange={(n) => set({ deviceHealthDays: n })} />
          <NumberField label="Audit logs" unit="days" value={v.auditLogsDays} onChange={(n) => set({ auditLogsDays: n })} />
          <NumberField
            label="Screenshots"
            unit="days"
            value={v.screenshotDays}
            onChange={(n) => set({ screenshotDays: n })}
            hint="Automatic periodic screenshots. The nightly job deletes the JPEG files as well as the rows."
          />
          <NumberField
            label="Flagged screenshots & clips"
            unit="days"
            value={v.flaggedScreenshotDays}
            onChange={(n) => set({ flaggedScreenshotDays: n })}
            hint="Captures triggered by a flagged site or app. Usually kept longer than periodic ones."
          />
        </div>
        {v.flaggedScreenshotDays > 0 && v.screenshotDays > 0 && v.flaggedScreenshotDays < v.screenshotDays && (
          <Notice tone="warn">
            Flagged captures are set to expire sooner than periodic ones. Flagged captures are the evidence of a policy
            breach, so they are normally kept longer.
          </Notice>
        )}
        <Button variant="primary" icon={Save} loading={save.pending} onClick={() => void save.run(v).then((r) => { if (r) { setSaved(true); onSaved(); } })}>Save retention</Button>
      </div>
    </Card>
  );
}

function ThresholdsForm({ initial, onSaved }: { initial: HealthThresholds; onSaved: () => void }) {
  const [v, setV] = useState(initial);
  const [saved, setSaved] = useState(false);
  const save = useAction(api.saveHealthThresholds);
  const set = (patch: Partial<HealthThresholds>) => { setSaved(false); setV((s) => ({ ...s, ...patch })); };
  const invalid = v.offlineSeconds <= v.heartbeatWarningSeconds;
  return (
    <Card title="Health thresholds">
      <div className="space-y-4">
        <p className="text-[13px] text-muted">The server evaluates device health every 15 seconds: heartbeat older than the warning threshold is WARNING, older than the offline threshold is OFFLINE.</p>
        {save.error && <Notice tone="danger">{save.error}</Notice>}
        {saved && <Notice tone="ok">Thresholds saved.</Notice>}
        {invalid && <Notice tone="warn">The offline threshold must be greater than the heartbeat warning threshold.</Notice>}
        <div className="grid gap-4 sm:grid-cols-2">
          <NumberField label="Heartbeat warning after" unit="s" min={10} value={v.heartbeatWarningSeconds} onChange={(n) => set({ heartbeatWarningSeconds: n })} />
          <NumberField label="Offline after" unit="s" min={30} value={v.offlineSeconds} onChange={(n) => set({ offlineSeconds: n })} />
          <NumberField label="Backlog warning" unit="queued events" min={1} value={v.backlogWarningThreshold} onChange={(n) => set({ backlogWarningThreshold: n })} />
          <Field label="Minimum agent version"><input className="field mono" value={v.minAgentVersion ?? ""} placeholder="1.0.0" onChange={(e) => set({ minAgentVersion: e.target.value.trim() || null })} /></Field>
        </div>
        <Button variant="primary" icon={Save} loading={save.pending} disabled={invalid} onClick={() => void save.run(v).then((r) => { if (r) { setSaved(true); onSaved(); } })}>Save thresholds</Button>
      </div>
    </Card>
  );
}

export function RetentionPanel() {
  const { data, error, loading, mutate } = useApi<RetentionSettings>("/settings/retention");
  return (
    <DataState loading={loading} error={error} onRetry={() => void mutate()}>
      {data && <RetentionForm key={JSON.stringify(data)} initial={data} onSaved={() => void mutate()} />}
    </DataState>
  );
}

export function ThresholdsPanel() {
  const { data, error, loading, mutate } = useApi<HealthThresholds>("/settings/health-thresholds");
  return (
    <DataState loading={loading} error={error} onRetry={() => void mutate()}>
      {data && <ThresholdsForm key={JSON.stringify(data)} initial={data} onSaved={() => void mutate()} />}
    </DataState>
  );
}
