import type { PageAccessMap, PageLevel } from "./types";

/** The parent page key for a sub-page key, else undefined. Keys are prefixed: device.* -> devices, settings.* -> settings. */
export function parentOf(key: string): string | undefined {
  if (key.startsWith("device.")) return "devices";
  if (key.startsWith("settings.")) return "settings";
  return undefined;
}

/**
 * The access level for a page/sub-page. A null map means unrestricted (EDIT everywhere). A sub-page with no explicit
 * entry inherits its parent's level; a top-level page with no entry is HIDDEN. Mirrors the server's RbacMapping.LevelOf.
 */
export function levelOf(map: PageAccessMap | null | undefined, key: string): PageLevel {
  if (map == null) return "EDIT";
  const own = map[key];
  if (own) return own;
  const parent = parentOf(key);
  if (parent && map[parent]) return map[parent];
  return "HIDDEN";
}

export const levelCanView = (lvl: PageLevel): boolean => lvl === "VIEW" || lvl === "EDIT";
export const levelCanEdit = (lvl: PageLevel): boolean => lvl === "EDIT";

export const canViewPage = (map: PageAccessMap | null | undefined, key: string): boolean => levelCanView(levelOf(map, key));
export const canEditPage = (map: PageAccessMap | null | undefined, key: string): boolean => levelCanEdit(levelOf(map, key));
