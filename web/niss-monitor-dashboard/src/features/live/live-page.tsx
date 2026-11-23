"use client";

import { useEffect, useMemo } from "react";
import { FilterBar } from "@/components/filters/filter-bar";
import { PageHeader } from "@/components/ui/primitives";
import { DataState, TableSkeleton } from "@/components/ui/states";
import { useFilters } from "@/hooks/use-filters";
import { filterLiveCards } from "@/lib/filters";
import { DEVICE_STATUSES } from "@/lib/types";
import { useMonitorHub } from "@/providers/signalr-provider";
import { DeviceCard } from "./device-card";
import { EventFeed } from "./event-feed";
import { useLiveCards } from "./use-live-cards";

const STATUS_ORDER: Record<string, number> = { WARNING: 0, OFFLINE: 1, SERVICE_STOPPED: 2, ONLINE: 3 };

export function LivePage() {
  const { subscribeFeed } = useMonitorHub();
  const { filters, setFilters } = useFilters();
  const { cards, loaded, loading, error, mutate } = useLiveCards();

  useEffect(() => subscribeFeed(), [subscribeFeed]);

  const visible = useMemo(
    () =>
      filterLiveCards(cards, filters).sort(
        (a, b) => (STATUS_ORDER[a.status] ?? 9) - (STATUS_ORDER[b.status] ?? 9) || a.hostname.localeCompare(b.hostname),
      ),
    [cards, filters],
  );

  return (
    <>
      <PageHeader
        title="Live Monitoring"
        subtitle="Real-time state of every enrolled PC. Cards update as heartbeats and events arrive - no refresh needed."
      />
      <FilterBar fields={["status", "department", "search"]} value={filters} onChange={setFilters} statusOptions={DEVICE_STATUSES} searchPlaceholder="Host, employee, application..." />
      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_340px] 2xl:grid-cols-[minmax(0,1fr)_380px]">
        <div className="min-w-0">
          <DataState
            loading={loading}
            error={error}
            onRetry={() => void mutate()}
            isEmpty={loaded && visible.length === 0}
            emptyTitle={cards.length === 0 ? "No devices enrolled yet" : "No devices match these filters"}
            emptyHint={cards.length === 0 ? "Create an enrollment token under Devices and install the agent on a PC." : undefined}
            skeleton={<TableSkeleton rows={4} cols={3} />}
          >
            <p className="mb-2 text-[12.5px] text-muted" aria-live="polite">{visible.length} device{visible.length === 1 ? "" : "s"}</p>
            <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-3">
              {visible.map((c) => (
                <DeviceCard key={c.deviceId} card={c} />
              ))}
            </div>
          </DataState>
        </div>
        <aside className="min-w-0 xl:sticky xl:top-20 xl:self-start">
          <EventFeed />
        </aside>
      </div>
    </>
  );
}
