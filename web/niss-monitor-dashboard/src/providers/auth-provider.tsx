"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { api, isNetworkError, setApiHooks } from "@/lib/api";
import { loadRuntimeConfig } from "@/lib/config";
import { roleAtLeast } from "@/lib/roles";
import { canEditPage, canViewPage, levelOf } from "@/lib/access";
import { tokenStore } from "@/lib/tokens";
import type { UserInfo } from "@/lib/types";

export type AuthStatus = "loading" | "authenticated" | "anonymous";

interface AuthContextValue {
  status: AuthStatus;
  user: UserInfo | null;
  /** Set while the API cannot be reached; carries the base URL that was tried. */
  unreachableUrl: string | null;
  /** true after the user pressed "Sign out" (so the login page does not bounce them back to the old page). */
  signedOut: boolean;
  isAdmin: boolean;
  isSuperAdmin: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  /** Re-run session bootstrap (used by the "Retry" button of the unreachable page). */
  retry: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>("loading");
  const [user, setUser] = useState<UserInfo | null>(null);
  const [unreachableUrl, setUnreachableUrl] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [signedOut, setSignedOut] = useState(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    setApiHooks({
      onAuthLost: () => {
        if (!mounted.current) return;
        setUser(null);
        setStatus("anonymous");
      },
      onReachability: (reachable, baseUrl) => {
        if (!mounted.current) return;
        setUnreachableUrl(reachable ? null : baseUrl);
      },
    });
    return () => {
      mounted.current = false;
      setApiHooks({});
    };
  }, []);

  // Bootstrap: load runtime config once, then restore the session from the refresh token (if any).
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      await loadRuntimeConfig();
      if (!tokenStore.getRefresh()) {
        if (!cancelled) setStatus("anonymous");
        // Reachability probe so a dead server is reported on the login page as well.
        try {
          await api.health();
        } catch {
          /* onReachability already recorded it */
        }
        return;
      }
      try {
        const me = await api.me();
        if (cancelled) return;
        setUser(me);
        setStatus("authenticated");
      } catch (e) {
        if (cancelled) return;
        if (isNetworkError(e)) {
          setStatus("anonymous");
        } else {
          tokenStore.clear();
          setStatus("anonymous");
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [attempt]);

  const login = useCallback(async (username: string, password: string) => {
    const auth = await api.login(username, password);
    tokenStore.set(auth);
    setSignedOut(false);
    setUser(auth.user);
    setStatus("authenticated");
  }, []);

  const logout = useCallback(async () => {
    await api.logout();
    tokenStore.clear();
    setSignedOut(true);
    setUser(null);
    setStatus("anonymous");
  }, []);

  const retry = useCallback(() => {
    setStatus("loading");
    setAttempt((n) => n + 1);
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      status,
      user,
      unreachableUrl,
      signedOut,
      isAdmin: roleAtLeast(user?.role, "ADMIN"),
      isSuperAdmin: roleAtLeast(user?.role, "SUPERADMIN"),
      login,
      logout,
      retry,
    }),
    [status, user, unreachableUrl, signedOut, login, logout, retry],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}

/** Per-user page/sub-page access for the signed-in user. SUPERADMIN (null map) can edit everything. */
export function usePageAccess() {
  const { user } = useAuth();
  const map = user?.pageAccess ?? null;
  return {
    level: (key: string) => levelOf(map, key),
    canView: (key: string) => canViewPage(map, key),
    canEdit: (key: string) => canEditPage(map, key),
  };
}
