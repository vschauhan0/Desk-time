import type { Role } from "./types";

const RANK: Record<Role, number> = { VIEWER: 1, ADMIN: 2, SUPERADMIN: 3 };

/** SUPERADMIN > ADMIN > VIEWER. Unknown roles rank 0. */
export function roleAtLeast(role: string | null | undefined, min: Role): boolean {
  const rank = RANK[role as Role] ?? 0;
  return rank >= RANK[min];
}
