import { formatDuration, humanize } from "./format";
import type { EventDto, JsonValue } from "./types";

/** Read a string property from an event payload (JsonElement). */
export function payloadString(payload: JsonValue, key: string): string | null {
  if (payload && typeof payload === "object" && !Array.isArray(payload)) {
    const v = payload[key];
    if (typeof v === "string" && v) return v;
    if (typeof v === "number") return String(v);
  }
  return null;
}

export function payloadNumber(payload: JsonValue, key: string): number | null {
  if (payload && typeof payload === "object" && !Array.isArray(payload)) {
    const v = payload[key];
    if (typeof v === "number") return v;
  }
  return null;
}

export interface EventDescription {
  title: string;
  detail: string | null;
}

/** One-line human description of a stored event, built only from telemetry fields (no content). */
export function describeEvent(event: EventDto): EventDescription {
  const p = event.payload;
  const proc = payloadString(p, "processName");
  const window = payloadString(p, "windowTitle");
  const user = payloadString(p, "userName");
  switch (event.eventType) {
    case "APPLICATION_FOREGROUND":
      return { title: `${proc ?? "Application"} in foreground`, detail: window };
    case "APPLICATION_BACKGROUND": {
      const d = payloadNumber(p, "durationSeconds");
      const i = payloadNumber(p, "interactionSeconds");
      return {
        title: `${proc ?? "Application"} left foreground`,
        detail: d !== null ? `foreground ${formatDuration(d)}${i !== null ? `, actively used ${formatDuration(i)}` : ""}` : null,
      };
    }
    case "APPLICATION_STARTED":
      return { title: `${proc ?? "Application"} opened`, detail: window };
    case "APPLICATION_STOPPED":
      return { title: `${proc ?? "Application"} closed`, detail: window };
    case "PROCESS_STARTED":
      return { title: `Process ${proc ?? ""} started`.trim(), detail: payloadString(p, "processId") ? `PID ${payloadString(p, "processId")}` : null };
    case "PROCESS_STOPPED":
      return { title: `Process ${proc ?? ""} stopped`.trim(), detail: payloadString(p, "processId") ? `PID ${payloadString(p, "processId")}` : null };
    case "USER_LOGIN":
    case "USER_LOGOUT":
      return { title: `${humanize(event.eventType)}${user ? `: ${user}` : ""}`, detail: null };
    case "IDLE_ENDED": {
      const s = payloadNumber(p, "idleSeconds");
      return { title: "Idle ended", detail: s !== null ? `idle for ${formatDuration(s)}` : null };
    }
    case "USB_CONNECTED":
    case "USB_DISCONNECTED":
      return { title: humanize(event.eventType), detail: [payloadString(p, "vendor"), payloadString(p, "product")].filter(Boolean).join(" ") || payloadString(p, "deviceType") };
    case "FILE_CREATED":
    case "FILE_MODIFIED":
    case "FILE_RENAMED":
    case "FILE_DELETED":
      return { title: humanize(event.eventType), detail: payloadString(p, "fileName") };
    case "AGENT_ERROR":
    case "AGENT_STARTED":
    case "AGENT_STOPPED":
    case "AGENT_VERSION_CHANGED":
      return { title: humanize(event.eventType), detail: payloadString(p, "message") ?? payloadString(p, "reason") ?? payloadString(p, "agentVersion") };
    default:
      return { title: humanize(event.eventType), detail: user };
  }
}
