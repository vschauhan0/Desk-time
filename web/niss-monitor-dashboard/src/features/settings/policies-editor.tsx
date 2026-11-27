"use client";

import { Plus, Save } from "lucide-react";
import { useMemo, useState } from "react";
import { Button, Card, Field, Notice, Pill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { Dialog, Time } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api } from "@/lib/api";
import { DEFAULT_POLICY, normalizePolicy, validatePolicy } from "@/lib/policy";
import type { AgentPolicy, PolicyDto, PolicyUpsert } from "@/lib/types";
import { ListField, NumberField, SectionTitle, ToggleField } from "./form-fields";
import { ScreenCaptureFields } from "./screen-capture-fields";

function PolicyForm({ policy, saving, error, onSave }: { policy: PolicyDto; saving: boolean; error: string | null; onSave: (body: PolicyUpsert) => void }) {
  const [name, setName] = useState(policy.name);
  // Normalised on load: a policy saved before screen capture existed arrives without those sub-objects.
  const [draft, setDraft] = useState<AgentPolicy>(() => normalizePolicy(policy.policy));
  const issues = useMemo(() => validatePolicy(draft), [draft]);
  const set = <K extends keyof AgentPolicy>(key: K, value: AgentPolicy[K]) => setDraft((d) => ({ ...d, [key]: value }));

  return (
    <div className="space-y-4">
      {error && <Notice tone="danger">{error}</Notice>}
      {issues.length > 0 && (
        <Notice tone="warn">
          <ul className="list-disc pl-4">{issues.map((i) => <li key={i.field}>{i.message}</li>)}</ul>
        </Notice>
      )}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <Field label="Policy name"><input className="field" value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Scope"><input className="field" value={`${policy.scope}${policy.scopeValue ? `: ${policy.scopeValue}` : ""}`} readOnly disabled /></Field>
        <div className="flex items-end text-[12px] text-faint">Most specific scope wins: device, then department, then global.</div>

        <SectionTitle>Timing</SectionTitle>
        <NumberField label="Heartbeat" unit="s" value={draft.heartbeatSeconds} min={5} onChange={(n) => set("heartbeatSeconds", n)} />
        <NumberField label="System metrics" unit="s" value={draft.metricsSeconds} min={5} onChange={(n) => set("metricsSeconds", n)} />
        <NumberField label="Foreground poll" unit="ms" value={draft.foregroundPollMilliseconds} min={250} onChange={(n) => set("foregroundPollMilliseconds", n)} />
        <NumberField label="Idle threshold" unit="min" value={draft.idleThresholdMinutes} min={1} hint="No input for longer than this = IDLE." onChange={(n) => set("idleThresholdMinutes", n)} />
        <NumberField label="Interaction window" unit="s" value={draft.interactionWindowSeconds} min={1} hint="Input within this window = INTERACTING." onChange={(n) => set("interactionWindowSeconds", n)} />
        <NumberField label="Input bucket" unit="s" value={draft.inputBucketSeconds} min={5} hint="Counts only - never key identities." onChange={(n) => set("inputBucketSeconds", n)} />

        <SectionTitle>Privacy (what is captured)</SectionTitle>
        <ToggleField label="Capture window titles" checked={draft.captureWindowTitles} onChange={(v) => set("captureWindowTitles", v)} hint="Window titles can contain document or page names." warn />
        <ToggleField label="Capture command lines" checked={draft.captureCommandLines} onChange={(v) => set("captureCommandLines", v)} hint="Command lines can contain file paths or arguments. Off by default." warn />
        <ToggleField label="Compute process hashes (SHA-256)" checked={draft.computeProcessHashes} onChange={(v) => set("computeProcessHashes", v)} hint="Extra disk I/O when processes start." />

        <SectionTitle>Processes</SectionTitle>
        <Field label="Process monitoring mode">
          <select className="field" value={draft.processMonitoringMode} onChange={(e) => set("processMonitoringMode", e.target.value)}>
            <option value="Interactive">Interactive (user-session processes only)</option>
            <option value="All">All processes</option>
          </select>
        </Field>
        <div className="sm:col-span-2">
          <ListField label="Process exclusions" value={draft.processExclusions} onChange={(v) => set("processExclusions", v)} placeholder={"svchost.exe\nsearchindexer.exe"} hint="One process name per line." />
        </div>

        <SectionTitle>Optional modules (disabled by default)</SectionTitle>
        <ToggleField label="USB monitoring" checked={draft.usbMonitoringEnabled} onChange={(v) => set("usbMonitoringEnabled", v)} />
        <ToggleField label="File monitoring" checked={draft.fileMonitoring.enabled} onChange={(v) => set("fileMonitoring", { ...draft.fileMonitoring, enabled: v })} hint="Metadata only (name, path, action). Never contents." warn />
        <ToggleField label="Cursor sampling" checked={draft.cursorSampling.enabled} onChange={(v) => set("cursorSampling", { ...draft.cursorSampling, enabled: v })} hint="Coarse position samples; off by default." warn />
        <div className="sm:col-span-1">
          <ListField label="Monitored folders" value={draft.fileMonitoring.paths} onChange={(v) => set("fileMonitoring", { ...draft.fileMonitoring, paths: v })} placeholder={"D:\\Shared\\Projects"} hint="One folder per line. Used only when file monitoring is on." />
        </div>
        <div className="sm:col-span-1">
          <ListField label="Ignored extensions" value={draft.fileMonitoring.ignoreExtensions} onChange={(v) => set("fileMonitoring", { ...draft.fileMonitoring, ignoreExtensions: v })} placeholder={".tmp\n.log"} />
        </div>
        <NumberField label="Cursor sampling interval" unit="s" value={draft.cursorSampling.intervalSeconds} min={1} onChange={(n) => set("cursorSampling", { ...draft.cursorSampling, intervalSeconds: n })} />

        <SectionTitle>Screen capture</SectionTitle>
        <ScreenCaptureFields
          value={draft.screenCapture}
          captureWindowTitles={draft.captureWindowTitles}
          onChange={(v) => set("screenCapture", v)}
        />

        <SectionTitle>Upload</SectionTitle>
        <NumberField label="Batch size" unit="events" value={draft.uploadBatchSize} min={1} max={500} onChange={(n) => set("uploadBatchSize", n)} />
        <NumberField label="Upload interval" unit="s" value={draft.uploadIntervalSeconds} min={1} onChange={(n) => set("uploadIntervalSeconds", n)} />
        <NumberField label="Backlog warning threshold" unit="events" value={draft.backlogWarningThreshold} min={1} onChange={(n) => set("backlogWarningThreshold", n)} />

        <SectionTitle>Agent versions</SectionTitle>
        <Field label="Minimum agent version"><input className="field mono" value={draft.minAgentVersion ?? ""} onChange={(e) => set("minAgentVersion", e.target.value.trim() || null)} placeholder="1.0.0" /></Field>
        <Field label="Latest agent version"><input className="field mono" value={draft.latestAgentVersion ?? ""} onChange={(e) => set("latestAgentVersion", e.target.value.trim() || null)} placeholder="1.2.0" /></Field>
      </div>
      <div className="flex items-center justify-between gap-3 border-t border-line pt-3">
        <span className="text-[12px] text-faint">Agents pick up changes on their next heartbeat (policy version {draft.version}).</span>
        <Button variant="primary" icon={Save} loading={saving} disabled={issues.length > 0 || !name.trim()} onClick={() => onSave({ name: name.trim(), scope: policy.scope, scopeValue: policy.scopeValue, policy: draft })}>
          Save policy
        </Button>
      </div>
    </div>
  );
}

function NewPolicyDialog({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [scope, setScope] = useState<"DEPARTMENT" | "DEVICE">("DEPARTMENT");
  const [scopeValue, setScopeValue] = useState("");
  const create = useAction(api.createPolicy);
  async function submit() {
    const res = await create.run({ name: name.trim(), scope, scopeValue: scopeValue.trim(), policy: DEFAULT_POLICY });
    if (res) {
      onCreated();
      onClose();
    }
  }
  return (
    <Dialog open={open} onClose={onClose} title="New scoped policy" footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" loading={create.pending} disabled={!name.trim() || !scopeValue.trim()} onClick={() => void submit()}>Create</Button></>}>
      <div className="space-y-3">
        {create.error && <Notice tone="danger">{create.error}</Notice>}
        <Field label="Name"><input className="field" value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Scope">
          <select className="field" value={scope} onChange={(e) => setScope(e.target.value as "DEPARTMENT" | "DEVICE")}>
            <option value="DEPARTMENT">Department</option>
            <option value="DEVICE">Single device</option>
          </select>
        </Field>
        <Field label={scope === "DEPARTMENT" ? "Department name" : "Device id"}><input className="field" value={scopeValue} onChange={(e) => setScopeValue(e.target.value)} /></Field>
        <p className="text-[12px] text-faint">Starts from the built-in defaults; adjust after creating.</p>
      </div>
    </Dialog>
  );
}

export function PoliciesEditor() {
  const { data, error, loading, mutate } = useApi<PolicyDto[]>("/settings/policies");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const save = useAction(async (id: string, body: PolicyUpsert) => {
    const res = await api.updatePolicy(id, body);
    await mutate();
    return res;
  });
  const [saved, setSaved] = useState(false);

  const selected = data?.find((p) => p.id === selectedId) ?? data?.find((p) => p.scope === "GLOBAL") ?? data?.[0];

  return (
    <DataState loading={loading} error={error} onRetry={() => void mutate()} isEmpty={!!data && data.length === 0} emptyTitle="No policies defined">
      {data && selected && (
        <div className="grid gap-5 lg:grid-cols-[260px_minmax(0,1fr)]">
          <Card title="Policies" padded={false} actions={<Button size="sm" icon={Plus} onClick={() => setCreating(true)}>New</Button>}>
            <ul className="divide-y divide-line" role="listbox" aria-label="Policies">
              {data.map((p) => (
                <li key={p.id}>
                  <button type="button" role="option" aria-selected={p.id === selected.id} onClick={() => { setSelectedId(p.id); setSaved(false); }} className={`w-full px-4 py-2.5 text-left hover:bg-surface-2 ${p.id === selected.id ? "bg-brand-soft" : ""}`}>
                    <span className="block text-[13px] font-medium">{p.name}</span>
                    <span className="mt-0.5 flex items-center gap-2 text-[11.5px] text-muted"><Pill tone="neutral" icon={false}>{p.scope}</Pill>{p.scopeValue}</span>
                    <span className="block text-[11px] text-faint">Updated <Time value={p.updatedAtUtc} /></span>
                  </button>
                </li>
              ))}
            </ul>
          </Card>
          <Card title={`Edit policy: ${selected.name}`}>
            {saved && <div className="mb-3"><Notice tone="ok">Policy saved.</Notice></div>}
            <PolicyForm
              key={`${selected.id}:${selected.updatedAtUtc}`}
              policy={selected}
              saving={save.pending}
              error={save.error}
              onSave={(body) => {
                void save.run(selected.id, body).then((r) => setSaved(r !== undefined));
              }}
            />
          </Card>
        </div>
      )}
      <NewPolicyDialog open={creating} onClose={() => setCreating(false)} onCreated={() => void mutate()} />
    </DataState>
  );
}
