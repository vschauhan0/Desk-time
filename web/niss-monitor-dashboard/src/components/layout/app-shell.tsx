"use client";

import clsx from "clsx";
import { LogOut, Menu, Moon, Radio, Sun, X } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { Button } from "@/components/ui/primitives";
import { ServerUnreachable } from "@/components/ui/states";
import { useAuth } from "@/providers/auth-provider";
import { SignalRProvider } from "@/providers/signalr-provider";
import { roleAtLeast } from "@/lib/roles";
import { useLiveStore, type HubState } from "@/store/live-store";
import { Brand } from "./brand";
import { NAV, NAV_ITEMS, isActivePath, isPageAllowed, pageKeyForPath } from "./nav";

// ----- Theme toggle ------------------------------------------------------------------------------

type Theme = "light" | "dark";

function readTheme(): Theme {
  return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
}

function subscribeTheme(cb: () => void): () => void {
  const obs = new MutationObserver(cb);
  obs.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  return () => obs.disconnect();
}

function ThemeToggle() {
  const theme = useSyncExternalStore(subscribeTheme, readTheme, () => "light" as Theme);
  const next: Theme = theme === "dark" ? "light" : "dark";
  return (
    <Button
      variant="ghost"
      iconOnly
      icon={theme === "dark" ? Sun : Moon}
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
      onClick={() => {
        document.documentElement.setAttribute("data-theme", next);
        try {
          localStorage.setItem("niss.theme", next);
        } catch {
          /* private mode */
        }
      }}
    />
  );
}

// ----- Live connection indicator -----------------------------------------------------------------

const HUB_LABEL: Record<HubState, { label: string; cls: string }> = {
  idle: { label: "Idle", cls: "text-faint" },
  connecting: { label: "Connecting", cls: "text-warn" },
  connected: { label: "Live", cls: "text-ok" },
  reconnecting: { label: "Reconnecting", cls: "text-warn" },
  disconnected: { label: "Offline", cls: "text-danger" },
};

export function ConnectionIndicator() {
  const state = useLiveStore((s) => s.hubState);
  const meta = HUB_LABEL[state];
  return (
    <span
      className={clsx("inline-flex items-center gap-1.5 rounded-full border border-line px-2.5 py-1 text-[12px] font-medium", meta.cls)}
      role="status"
      aria-live="polite"
      title={`Real-time connection: ${meta.label}`}
    >
      <Radio size={13} className={state === "connected" ? "" : "pulse"} aria-hidden />
      {meta.label}
    </span>
  );
}

// ----- Sidebar -----------------------------------------------------------------------------------

function Sidebar({ onNavigate }: { onNavigate?: () => void }) {
  const pathname = usePathname();
  const { user } = useAuth();
  return (
    <nav aria-label="Main" className="flex h-full flex-col gap-4 overflow-y-auto px-3 py-4">
      <div className="px-2 pb-1">
        <Brand />
      </div>
      {NAV.map((group) => {
        const items = group.items.filter(
          (i) => (!i.minRole || roleAtLeast(user?.role, i.minRole)) && isPageAllowed(user?.pageAccess, i.key),
        );
        if (items.length === 0) return null;
        return (
          <div key={group.title}>
            <p className="label !mb-1 px-2 !text-[10.5px] !text-faint">{group.title}</p>
            <ul className="space-y-0.5">
              {items.map((item) => {
                const active = isActivePath(pathname, item.href);
                return (
                  <li key={item.href}>
                    <Link
                      href={item.href}
                      onClick={onNavigate}
                      aria-current={active ? "page" : undefined}
                      className={clsx(
                        "flex items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-[13px] font-medium",
                        active ? "bg-brand-soft text-brand" : "text-muted hover:bg-surface-2 hover:text-fg",
                      )}
                    >
                      <item.icon size={16} aria-hidden />
                      {item.label}
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        );
      })}
      <p className="mt-auto px-2 text-[11px] leading-snug text-faint">
        Transparent, organisation-managed monitoring. Objective telemetry only.
      </p>
    </nav>
  );
}

// ----- Shell -------------------------------------------------------------------------------------

function Shell({ children }: { children: ReactNode }) {
  const { user, logout, unreachableUrl } = useAuth();
  const [drawer, setDrawer] = useState(false);

  return (
    <div className="flex min-h-screen">
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:left-2 focus:top-2 focus:z-50 focus:rounded-lg focus:bg-brand focus:px-3 focus:py-2 focus:text-on-brand">
        Skip to content
      </a>

      <aside className="no-print sticky top-0 hidden h-screen w-60 shrink-0 border-r border-line bg-surface lg:block">
        <Sidebar />
      </aside>

      {drawer && (
        <div className="no-print fixed inset-0 z-40 lg:hidden" role="dialog" aria-modal="true" aria-label="Navigation">
          <div className="absolute inset-0 bg-black/50" onClick={() => setDrawer(false)} />
          <div className="absolute inset-y-0 left-0 w-64 bg-surface shadow-xl">
            <Button variant="ghost" iconOnly icon={X} className="absolute right-2 top-2" aria-label="Close menu" onClick={() => setDrawer(false)} />
            <Sidebar onNavigate={() => setDrawer(false)} />
          </div>
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="no-print sticky top-0 z-30 flex h-14 items-center gap-2 border-b border-line bg-surface/90 px-4 backdrop-blur">
          <Button variant="ghost" iconOnly icon={Menu} className="lg:hidden" aria-label="Open menu" onClick={() => setDrawer(true)} />
          <div className="lg:hidden">
            <Brand compact />
          </div>
          <div className="ml-auto flex items-center gap-2">
            <ConnectionIndicator />
            <ThemeToggle />
            {user && (
              <div className="ml-1 hidden items-center gap-2 border-l border-line pl-3 sm:flex">
                <div className="text-right leading-tight">
                  <p className="text-[13px] font-medium">{user.displayName}</p>
                  <p className="text-[10.5px] font-semibold uppercase tracking-wider text-muted">{user.role}</p>
                </div>
              </div>
            )}
            <Button
              variant="ghost"
              iconOnly
              icon={LogOut}
              aria-label="Sign out"
              title="Sign out"
              onClick={() => void logout()}
            />
          </div>
        </header>

        {unreachableUrl && (
          <div className="no-print border-b border-danger/40 bg-danger-soft px-4 py-2 text-[13px] text-danger" role="alert">
            Connection to the NISS server at <span className="mono">{unreachableUrl}</span> was lost. Live data reconnects automatically; other views may be stale until you reload them.
          </div>
        )}

        <main id="main" tabIndex={-1} className="app-main mx-auto w-full max-w-[1500px] flex-1 p-4 sm:p-6">
          <PageAccessGuard>{children}</PageAccessGuard>
        </main>
      </div>
    </div>
  );
}

/**
 * Blocks a page the signed-in user is not allowed to open (by role or by their per-user page access), redirecting to
 * their first available page. A detail route not in the catalogue is allowed here; its data is scoped server-side.
 */
function PageAccessGuard({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const pathname = usePathname();
  const router = useRouter();
  const key = pageKeyForPath(pathname);
  const item = NAV_ITEMS.find((i) => i.key === key);
  const allowed = !item || ((!item.minRole || roleAtLeast(user?.role, item.minRole)) && isPageAllowed(user?.pageAccess, key));
  const fallback = NAV_ITEMS.find((i) => (!i.minRole || roleAtLeast(user?.role, i.minRole)) && isPageAllowed(user?.pageAccess, i.key));

  useEffect(() => {
    if (!allowed && fallback && fallback.href !== pathname) router.replace(fallback.href);
  }, [allowed, fallback, pathname, router]);

  if (allowed) return <>{children}</>;
  return (
    <div className="grid min-h-[50vh] place-items-center text-center">
      <div>
        <p className="text-[15px] font-semibold">This page is not available to your account</p>
        <p className="mt-1 text-[13px] text-muted">Your administrator controls which pages you can open.</p>
      </div>
    </div>
  );
}

/** Guard for everything behind login: bootstrap state, redirect, unreachable page, SignalR. */
export function AppGate({ children }: { children: ReactNode }) {
  const { status, unreachableUrl, signedOut, retry } = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (status === "anonymous" && !unreachableUrl) {
      router.replace(signedOut ? "/login" : `/login?next=${encodeURIComponent(pathname)}`);
    }
  }, [status, unreachableUrl, signedOut, router, pathname]);

  if (status !== "authenticated") {
    if (unreachableUrl) return <ServerUnreachable url={unreachableUrl} onRetry={retry} fullPage />;
    return (
      <div className="flex min-h-screen items-center justify-center" aria-busy="true">
        <Brand />
      </div>
    );
  }

  return (
    <SignalRProvider>
      <Shell>{children}</Shell>
    </SignalRProvider>
  );
}
