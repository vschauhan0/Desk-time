"use client";

import { LayoutGrid, PanelRightClose, PanelRightOpen, Table2 } from "lucide-react";
import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { Button, PageHeader } from "@/components/ui/primitives";
import { DataState, TableSkeleton } from "@/components/ui/states";
import { useFilters } from "@/hooks/use-filters";
import { filterLiveCards } from "@/lib/filters";
import { DEVICE_STATUSES, type LiveDeviceCard } from "@/lib/types";
import { useMonitorHub } from "@/providers/signalr-provider";
import { DeviceCard } from "./device-card";
import { EventFeed } from "./event-feed";
import { LiveTable } from "./live-table";
import { useLiveCards } from "./use-live-cards";

const STATUS_ORDER: Record<string, number> = { WARNING: 0, OFFLINE: 1, SERVICE_STOPPED: 2, ONLINE: 3 };

type View = "table" | "cards";

// localStorage-backed view preference via useSyncExternalStore (same pattern as the theme toggle): no setState in an
// effect, and SSR/hydration agree because the server snapshot is the default.
const VIEW_KEY = "niss.live.view";
const viewListeners = new Set<() => void>();
function subscribeView(cb: () => void): () => void {
  viewListeners.add(cb);
  return () => viewListeners.delete(cb);
}
function readView(): View {
  try {
    return localStorage.getItem(VIEW_KEY) === "cards" ? "cards" : "table";
  } catch {
    return "table";
  }
}
function usePersistentView(): [View, (v: View) => void] {
  const view = useSyncExternalStore(subscribeView, readView, () => "table" as View);
  const set = (v: View) => {
    try {
      localStorage.setItem(VIEW_KEY, v);
    } catch {
      /* private mode */
    }
    viewListeners.forEach((l) => l());
  };
  return [view, set];
}

/** A row of status counts that gives the fleet's shape before you read any single device. */
function StatusStrip({ cards }: { cards: LiveDeviceCard[] }) {
  const n = useMemo(() => {
    const c = { online: 0, warning: 0, offline: 0, other: 0, active: 0, idle: 0, locked: 0 };
    for (const d of cards) {
      if (d.status === "ONLINE") c.online++;
      else if (d.status === "WARNING") c.warning++;
      else if (d.status === "OFFLINE" || d.status === "SERVICE_STOPPED") c.offline++;
      else c.other++;
      if (d.isLocked) c.locked++;
      else if (d.isIdle) c.idle++;
      else if (d.presence === "ACTIVE") c.active++;
    }
    return c;
  }, [cards]);

  const items: { label: string; value: number; dot: string }[] = [
    { label: "Online", value: n.online, dot: "var(--ok)" },
    { label: "Warning", value: n.warning, dot: "var(--warn)" },
    { label: "Offline", value: n.offline, dot: "var(--danger)" },
    { label: "Active", value: n.active, dot: "var(--c1)" },
    { label: "Idle", value: n.idle, dot: "var(--c2)" },
    { label: "Locked", value: n.locked, dot: "var(--c3)" },
  ];
  return (
    <div className="flex flex-wrap items-center gap-x-5 gap-y-2 rounded-xl border border-line bg-surface px-4 py-2.5">
      <span className="text-[13px] font-semibold">{cards.length} device{cards.length === 1 ? "" : "s"}</span>
      <span className="h-4 w-px bg-line" aria-hidden />
      {items.map((it) => (
        <span key={it.label} className="inline-flex items-center gap-1.5 text-[12.5px]">
          <span className="size-2.5 rounded-full" style={{ background: it.dot }} aria-hidden />
          <span className="tabular-nums font-medium">{it.value}</span>
          <span className="text-muted">{it.label}</span>
        </span>
      ))}
    </div>
  );
}

export function LivePage() {
  const { subscribeFeed } = useMonitorHub();
  const { filters, setFilters } = useFilters();
  const { cards, loaded, loading, error, mutate } = useLiveCards();
  const [view, setView] = usePersistentView();
  const [feedOpen, setFeedOpen] = useState(false);

  useEffect(() => subscribeFeed(), [subscribeFeed]);

  const visible = useMemo(
    () =>
      filterLiveCards(cards, filters).sort(
        (a, b) => (STATUS_ORDER[a.status] ?? 9) - (STATUS_ORDER[b.status] ?? 9) || a.hostname.localeCompare(b.hostname),
      ),
    [cards, filters],
  );

  const toggle = (
    <div className="flex items-center gap-1 rounded-lg border border-line p-0.5" role="group" aria-label="View">
      <button type="button" onClick={() => setView("table")} aria-pressed={view === "table"} title="Table" className={`flex items-center gap-1.5 rounded-md px-2.5 py-1 text-[12.5px] font-medium ${view === "table" ? "bg-brand-soft text-brand" : "text-muted hover:text-fg"}`}>
        <Table2 size={14} aria-hidden /> Table
      </button>
      <button type="button" onClick={() => setView("cards")} aria-pressed={view === "cards"} title="Cards" className={`flex items-center gap-1.5 rounded-md px-2.5 py-1 text-[12.5px] font-medium ${view === "cards" ? "bg-brand-soft text-brand" : "text-muted hover:text-fg"}`}>
        <LayoutGrid size={14} aria-hidden /> Cards
      </button>
    </div>
  );

  return (
    <div className="flex h-full flex-col gap-3">
      <PageHeader
        title="Live Monitoring"
        subtitle="Real-time state of every enrolled PC. Updates as heartbeats and events arrive — no refresh needed."
        actions={
          <>
            {toggle}
            <Button variant="ghost" icon={feedOpen ? PanelRightClose : PanelRightOpen} onClick={() => setFeedOpen((o) => !o)}>
              {feedOpen ? "Hide feed" : "Live feed"}
            </Button>
          </>
        }
      />
      <FilterBar fields={["status", "department", "search"]} value={filters} onChange={setFilters} statusOptions={DEVICE_STATUSES} searchPlaceholder="Host, employee, application..." />

      {loaded && cards.length > 0 && <StatusStrip cards={visible} />}

      <div className={`grid min-h-0 flex-1 gap-4 ${feedOpen ? "xl:grid-cols-[minmax(0,1fr)_340px] 2xl:grid-cols-[minmax(0,1fr)_380px]" : "grid-cols-1"}`}>
        <div className="flex min-h-0 min-w-0 flex-col">
          <DataState
            loading={loading}
            error={error}
            onRetry={() => void mutate()}
            isEmpty={loaded && visible.length === 0}
            emptyTitle={cards.length === 0 ? "No devices enrolled yet" : "No devices match these filters"}
            emptyHint={cards.length === 0 ? "Create an enrollment token under Devices and install the agent on a PC." : undefined}
            skeleton={<TableSkeleton rows={6} cols={5} />}
          >
            {view === "table" ? (
              <LiveTable cards={visible} />
            ) : (
              <div className="min-h-0 flex-1 overflow-auto">
                <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-3">
                  {visible.map((c) => (
                    <DeviceCard key={c.deviceId} card={c} />
                  ))}
                </div>
              </div>
            )}
          </DataState>
        </div>
        {feedOpen && (
          <aside className="hidden min-w-0 xl:block xl:sticky xl:top-20 xl:self-start">
            <EventFeed height="max-h-[calc(100dvh-230px)]" />
          </aside>
        )}
      </div>
    </div>
  );
}
