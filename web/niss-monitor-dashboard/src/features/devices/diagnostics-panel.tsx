"use client";

import { Download } from "lucide-react";
import type { ReactNode } from "react";
import { Button, Card, Notice, Pill, StatusPill } from "@/components/ui/primitives";
import { DataState } from "@/components/ui/states";
import { MiniBar, RelativeTime, Time } from "@/components/ui/widgets";
import { useAction } from "@/hooks/use-action";
import { useApi } from "@/hooks/use-api";
import { api, saveBlob } from "@/lib/api";
import { formatNumber } from "@/lib/format";
import { collectorStateTone, loadTone, severityTone } from "@/lib/status";
import type { DeviceDiagnostics } from "@/lib/types";
import { useAuth } from "@/providers/auth-provider";

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-line py-2 last:border-0">
      <dt className="text-[12.5px] text-muted">{label}</dt>
      <dd className="min-w-0 break-words text-right text-[13px]">{children}</dd>
    </div>
  );
}

function stateTone(state: string) {
  const s = state.toUpperCase();
  if (["RUNNING", "OK", "CONNECTED", "ACTIVE"].includes(s)) return "ok" as const;
  if (["STOPPED", "MISSING", "FAILED", "ERROR"].includes(s)) return "danger" as const;
  return "neutral" as const;
}

export function DiagnosticsPanel({ deviceId }: { deviceId: string }) {
  const { isAdmin } = useAuth();
  const { data, error, loading, mutate } = useApi<DeviceDiagnostics>(`/devices/${encodeURIComponent(deviceId)}/diagnostics`, undefined, { refreshInterval: 30_000 });
  const dl = useAction(async () => {
    const { blob, filename } = await api.downloadDiagnostics(deviceId);
    saveBlob(blob, filename);
  });

  return (
    <DataState loading={loading} error={error} onRetry={() => void mutate()}>
      {data && (
        <div className="space-y-5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-[13px] text-muted">Agent and server-side health for {data.hostname}. Refreshes every 30 seconds.</p>
            {isAdmin && (
              <Button icon={Download} loading={dl.pending} onClick={() => void dl.run()}>
                Download Diagnostics
              </Button>
            )}
          </div>
          {dl.error && <Notice tone="danger">{dl.error}</Notice>}
          {data.lastError && <Notice tone="warn">Last agent error: {data.lastError}</Notice>}

          <div className="grid gap-5 lg:grid-cols-2">
            <Card title="Agent">
              <dl>
                <Row label="Agent version"><span className="mono">{data.agentVersion}</span></Row>
                <Row label="Service state"><Pill tone={stateTone(data.serviceState)}>{data.serviceState}</Pill></Row>
                <Row label="Session agent state"><Pill tone={stateTone(data.sessionAgentState)}>{data.sessionAgentState}</Pill></Row>
                <Row label="Operating system">{data.osVersion ?? "—"}</Row>
                <Row label="Clock offset vs server">{formatNumber(data.clockOffsetMs)} ms</Row>
              </dl>
              {data.sessionAgents.length > 0 && (
                <div className="mt-3">
                  <p className="label">Session agents</p>
                  <ul className="space-y-1 text-[12.5px]">
                    {data.sessionAgents.map((s) => (
                      <li key={s.windowsSessionId} className="flex items-center justify-between gap-2">
                        <span>Session {s.windowsSessionId}{s.userName ? ` - ${s.userName}` : ""}</span>
                        <span className="flex items-center gap-2">
                          <Pill tone={s.connected ? "ok" : "danger"}>{s.connected ? "Connected" : "Disconnected"}</Pill>
                          <span className="text-faint"><RelativeTime value={s.lastSeenUtc} /></span>
                        </span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </Card>

            <Card title="Connectivity & queue">
              <dl>
                <Row label="Last heartbeat"><RelativeTime value={data.lastHeartbeatUtc} /></Row>
                <Row label="Server connection"><Pill tone={data.serverConnection ? "ok" : "danger"}>{data.serverConnection ? "Reachable" : "Unreachable"}</Pill></Row>
                <Row label="Queue count">{formatNumber(data.queueCount)}</Row>
                <Row label="Dead-letter count"><span className={data.deadLetterCount > 0 ? "font-semibold text-warn" : ""}>{formatNumber(data.deadLetterCount)}</span></Row>
                <Row label="Last successful upload"><Time value={data.lastSuccessfulUploadUtc} /></Row>
                <Row label="Last error">{data.lastError ?? "None"}</Row>
              </dl>
            </Card>

            <Card title="Resources">
              <dl>
                <Row label="CPU"><MiniBar percent={data.cpuPercent} tone={loadTone(data.cpuPercent)} /></Row>
                <Row label="Memory"><MiniBar percent={data.memoryUsedPercent} tone={loadTone(data.memoryUsedPercent)} /></Row>
                <Row label="Disk"><MiniBar percent={data.diskUsedPercent} tone={loadTone(data.diskUsedPercent)} /></Row>
              </dl>
            </Card>

            <Card title="Collectors" padded={false}>
              {data.collectors.length === 0 ? (
                <p className="p-4 text-[13px] text-muted">The agent has not reported collector status yet.</p>
              ) : (
                <div className="table-wrap !max-h-72 !rounded-none !border-0">
                  <table className="data-table">
                    <thead>
                      <tr><th>Collector</th><th>State</th><th>Last event</th><th>Last error</th></tr>
                    </thead>
                    <tbody>
                      {data.collectors.map((c) => (
                        <tr key={c.name}>
                          <td className="font-medium">{c.name}</td>
                          <td><StatusPill value={c.state} tone={collectorStateTone(c.state)} /></td>
                          <td><Time value={c.lastEventUtc} /></td>
                          <td className="text-muted">{c.lastError ?? "—"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Card>
          </div>

          <Card title="Recent issues" padded={false}>
            {data.recentIssues.length === 0 ? (
              <p className="p-4 text-[13px] text-muted">No recent issues for this device.</p>
            ) : (
              <ul className="divide-y divide-line">
                {data.recentIssues.map((i) => (
                  <li key={i.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5">
                    <StatusPill value={i.severity} tone={severityTone(i.severity)} />
                    <span className="font-medium">{i.kind}</span>
                    <span className="min-w-0 flex-1 text-muted">{i.message}</span>
                    <Time value={i.raisedAtUtc} className="text-[12px] text-faint" />
                    {i.resolvedAtUtc && <Pill tone="ok">Resolved</Pill>}
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      )}
    </DataState>
  );
}
