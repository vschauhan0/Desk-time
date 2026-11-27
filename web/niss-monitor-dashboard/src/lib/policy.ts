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
  screenCapture: {
    enabled: false,
    intervalSeconds: 300,
    quality: 60,
    maxWidth: 1600,
    liveEnabled: true,
    flaggedActivity: { enabled: false, terms: [], cooldownSeconds: 120, captureClip: true, clipSeconds: 10, clipFps: 2 },
  },
  usbMonitoringEnabled: true,
  uploadBatchSize: 200,
  uploadIntervalSeconds: 5,
  backlogWarningThreshold: 5000,
  minAgentVersion: null,
  latestAgentVersion: null,
};

/**
 * Fills in sub-policies the server may not have sent, so the editor always works on a complete object.
 *
 * The current server normalises these itself, but a policy row written before screen capture existed deserialises
 * `screenCapture.flaggedActivity` as null — and an older server will still send that null. Treat anything missing as
 * the shipped default, which means "off".
 */
export function normalizePolicy(policy: AgentPolicy): AgentPolicy {
  const screen = policy.screenCapture ?? DEFAULT_POLICY.screenCapture;
  return {
    ...policy,
    screenCapture: {
      ...screen,
      flaggedActivity: screen.flaggedActivity ?? DEFAULT_POLICY.screenCapture.flaggedActivity,
    },
  };
}

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
  issues.push(...validateScreenCapture(p));
  return issues;
}

/** Screen capture + flagged activity checks. Mirrors ScreenCapturePolicyValidator / FlaggedActivityPolicyValidator. */
function validateScreenCapture(p: AgentPolicy): PolicyIssue[] {
  const issues: PolicyIssue[] = [];
  const sc = p.screenCapture;
  if (!sc) return issues;

  if (sc.intervalSeconds < 5) issues.push({ field: "screenCapture.intervalSeconds", message: "Screenshot interval must be at least 5 seconds." });
  if (sc.quality < 1 || sc.quality > 100) issues.push({ field: "screenCapture.quality", message: "Screenshot quality must be between 1 and 100." });
  if (sc.maxWidth < 320 || sc.maxWidth > 7680) issues.push({ field: "screenCapture.maxWidth", message: "Screenshot max width must be between 320 and 7680 pixels." });

  const fa = sc.flaggedActivity;
  if (!fa) return issues;

  if (fa.enabled && fa.terms.length === 0) {
    issues.push({ field: "screenCapture.flaggedActivity.terms", message: "Flagged capture is enabled but no flagged sites or apps are listed." });
  }
  if (fa.terms.length > MAX_FLAGGED_TERMS) {
    issues.push({ field: "screenCapture.flaggedActivity.terms", message: `A policy is limited to ${MAX_FLAGGED_TERMS} flagged terms.` });
  }
  if (fa.clipSeconds < 1 || fa.clipSeconds > 60) issues.push({ field: "screenCapture.flaggedActivity.clipSeconds", message: "Clip length must be between 1 and 60 seconds." });
  if (fa.clipFps < 1 || fa.clipFps > 5) issues.push({ field: "screenCapture.flaggedActivity.clipFps", message: "Clip frame rate must be between 1 and 5 fps." });
  if (fa.cooldownSeconds < 0) issues.push({ field: "screenCapture.flaggedActivity.cooldownSeconds", message: "Cooldown cannot be negative." });

  // Sites are recognised from the window title, so flagged site matching silently stops working without it.
  if (fa.enabled && !p.captureWindowTitles) {
    issues.push({
      field: "captureWindowTitles",
      message: "Flagged capture matches window titles, but “Capture window titles” is off — only process-name terms will match.",
    });
  }
  return issues;
}

/** Hard ceiling on frames in one clip (mirrors `FlaggedActivityPolicy.MaxClipFrames` on the server). */
export const MAX_CLIP_FRAMES = 120;

/** Most flagged terms one policy may carry (mirrors FlaggedActivityPolicyValidator.MaxTerms). */
export const MAX_FLAGGED_TERMS = 200;

/** Frames one clip will produce, clamped the same way the agent clamps it. */
export function clipFrameCount(clipSeconds: number, clipFps: number): number {
  const seconds = Math.min(Math.max(clipSeconds, 1), 60);
  const fps = Math.min(Math.max(clipFps, 1), 5);
  return Math.min(Math.max(seconds * fps, 1), MAX_CLIP_FRAMES);
}
