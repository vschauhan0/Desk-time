import type { SecurityEventDto } from "@/lib/types";

/**
 * Security-event kinds that mean someone may be tampering with the agent, as opposed to ordinary health noise:
 * a deliberate stop while a user was signed in (TAMPER_SUSPECTED, Critical) and an unclean exit / kill / crash
 * (UNEXPECTED_SHUTDOWN). A plain SERVICE_STOPPED (e.g. a normal shutdown report) is intentionally NOT tamper.
 */
export const TAMPER_KINDS = ["TAMPER_SUSPECTED", "UNEXPECTED_SHUTDOWN"] as const;

export function isTamperKind(kind: string): boolean {
  return (TAMPER_KINDS as readonly string[]).includes(kind);
}

/** Unresolved tamper-class events, input order preserved (the API returns newest-first). */
export function unresolvedTamper(events: SecurityEventDto[]): SecurityEventDto[] {
  return events.filter((e) => !e.resolvedAtUtc && isTamperKind(e.kind));
}
