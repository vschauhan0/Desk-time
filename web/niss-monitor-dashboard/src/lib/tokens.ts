import type { AuthResponse } from "./types";

/**
 * Token storage - security tradeoff (documented in README):
 *  - The short-lived (15 min) ACCESS token lives only in JS memory. It is never written to disk/storage,
 *    so it cannot be lifted from storage by a stray script or a shared profile.
 *  - The rotating (8 h) REFRESH token lives in sessionStorage so an F5 / tab reload does not force a new
 *    login, while closing the tab drops the session. sessionStorage is readable by any script running on
 *    this origin (XSS), which is why the token rotates on every use and the app ships no third-party scripts.
 *    An HttpOnly cookie would be safer against XSS but would require same-site API hosting and CSRF handling,
 *    which the LAN topology (dashboard :3000, API :8443) does not offer.
 */

const REFRESH_KEY = "niss.refreshToken";

let accessToken: string | null = null;
let expiresAtMs = 0;

function storage(): Storage | null {
  try {
    return typeof window === "undefined" ? null : window.sessionStorage;
  } catch {
    return null;
  }
}

export const tokenStore = {
  getAccess(): string | null {
    return accessToken;
  },

  getRefresh(): string | null {
    try {
      return storage()?.getItem(REFRESH_KEY) ?? null;
    } catch {
      return null;
    }
  },

  set(auth: Pick<AuthResponse, "accessToken" | "refreshToken" | "expiresAtUtc">): void {
    accessToken = auth.accessToken;
    const parsed = Date.parse(auth.expiresAtUtc);
    expiresAtMs = Number.isNaN(parsed) ? Date.now() + 10 * 60_000 : parsed;
    try {
      storage()?.setItem(REFRESH_KEY, auth.refreshToken);
    } catch {
      /* storage unavailable: session survives until reload only */
    }
  },

  clear(): void {
    accessToken = null;
    expiresAtMs = 0;
    try {
      storage()?.removeItem(REFRESH_KEY);
    } catch {
      /* ignore */
    }
  },

  /** True when there is no access token, or it expires within `skewMs`. */
  needsRefresh(skewMs = 30_000): boolean {
    return accessToken === null || Date.now() + skewMs >= expiresAtMs;
  },
};
