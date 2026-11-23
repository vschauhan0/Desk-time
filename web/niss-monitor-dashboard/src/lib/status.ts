/** Mapping of API status strings to visual tones. Colour is never the only signal: pills always carry icon + label. */

export type Tone = "ok" | "warn" | "danger" | "info" | "neutral" | "brand";

export function deviceStatusTone(status: string | null | undefined): Tone {
  switch (status) {
    case "ONLINE":
      return "ok";
    case "WARNING":
      return "warn";
    case "OFFLINE":
    case "SERVICE_STOPPED":
    case "REVOKED":
      return "danger";
    case "PENDING":
      return "info";
    case "DISABLED":
    default:
      return "neutral";
  }
}

export function presenceTone(presence: string | null | undefined): Tone {
  switch (presence) {
    case "ACTIVE":
      return "ok";
    case "IDLE":
      return "warn";
    case "LOCKED":
      return "info";
    default:
      return "neutral";
  }
}

/** Severity strings are not enumerated by the contract; match common spellings case-insensitively. */
export function severityTone(severity: string | null | undefined): Tone {
  const s = (severity ?? "").toLowerCase();
  if (s.startsWith("crit") || s === "high" || s === "error") return "danger";
  if (s.startsWith("warn") || s === "medium") return "warn";
  if (s === "info" || s === "low" || s === "information") return "info";
  return "neutral";
}

export function collectorStateTone(state: string | null | undefined): Tone {
  const s = (state ?? "").toLowerCase();
  if (["running", "ok", "healthy", "started", "active", "enabled"].includes(s)) return "ok";
  if (["error", "failed", "faulted", "stopped"].includes(s)) return "danger";
  if (["degraded", "warning", "starting"].includes(s)) return "warn";
  return "neutral";
}

/** Health tone of a 0..100 utilisation figure. */
export function loadTone(percent: number | null | undefined): Tone {
  if (percent === null || percent === undefined) return "neutral";
  if (percent >= 90) return "danger";
  if (percent >= 75) return "warn";
  return "ok";
}
