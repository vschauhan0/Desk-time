import type { AgentPolicy } from "./types";

/** Defaults mirror the C# `AgentPolicy` record initialisers. */
export const DEFAULT_POLICY: AgentPolicy = {
  version: 1,
  heartbeatSeconds: 30,
  metricsSeconds: 30,
  foregroundPollMilliseconds: 1000,
  idleThresholdMinutes: 5,
  interactionWindowSeconds: 30,
  inputBucketSeconds: 30,
  captureWindowTitles: true,
  captureCommandLines: false,
  computeProcessHashes: false,
  processMonitoringMode: "Interactive",
  processExclusions: [],
  fileMonitoring: { enabled: false, paths: [], ignoreExtensions: [] },
  cursorSampling: { enabled: false, intervalSeconds: 5 },
  usbMonitoringEnabled: true,
  uploadBatchSize: 200,
  uploadIntervalSeconds: 5,
  backlogWarningThreshold: 5000,
  minAgentVersion: null,
  latestAgentVersion: null,
};

export interface PolicyIssue {
  field: string;
  message: string;
}

/** Client-side sanity checks before PUT (the server remains authoritative). */
export function validatePolicy(p: AgentPolicy): PolicyIssue[] {
  const issues: PolicyIssue[] = [];
  const positive: [keyof AgentPolicy, string][] = [
    ["heartbeatSeconds", "Heartbeat interval"],
    ["metricsSeconds", "Metrics interval"],
    ["foregroundPollMilliseconds", "Foreground poll"],
    ["idleThresholdMinutes", "Idle threshold"],
    ["interactionWindowSeconds", "Interaction window"],
    ["inputBucketSeconds", "Input bucket"],
    ["uploadBatchSize", "Upload batch size"],
    ["uploadIntervalSeconds", "Upload interval"],
    ["backlogWarningThreshold", "Backlog warning threshold"],
  ];
  for (const [key, label] of positive) {
    const v = p[key];
    if (typeof v !== "number" || v < 1) issues.push({ field: key, message: `${label} must be at least 1.` });
  }
  if (p.uploadBatchSize > 500) issues.push({ field: "uploadBatchSize", message: "Upload batch size is limited to 500 events by the server." });
  if (p.foregroundPollMilliseconds < 250) issues.push({ field: "foregroundPollMilliseconds", message: "Foreground poll below 250 ms adds needless CPU load." });
  if (p.fileMonitoring.enabled && p.fileMonitoring.paths.length === 0) issues.push({ field: "fileMonitoring.paths", message: "File monitoring is enabled but no folders are listed." });
  if (p.cursorSampling.enabled && p.cursorSampling.intervalSeconds < 1) issues.push({ field: "cursorSampling.intervalSeconds", message: "Cursor sampling interval must be at least 1 second." });
  return issues;
}
