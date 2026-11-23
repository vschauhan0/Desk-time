import { create } from "zustand";
import type { DeviceStatusChange, EventDto, LiveDeviceCard, SecurityEventDto } from "@/lib/types";

export type HubState = "idle" | "connecting" | "connected" | "reconnecting" | "disconnected";

export const FEED_LIMIT = 300;
export const ALERT_LIMIT = 50;

// ----- Pure helpers (unit-tested) ---------------------------------------------------------------

export function indexCards(cards: LiveDeviceCard[]): Record<string, LiveDeviceCard> {
  const out: Record<string, LiveDeviceCard> = {};
  for (const c of cards) out[c.deviceId] = c;
  return out;
}

/** Apply a DeviceStatusChanged push to the card map. Unknown devices are ignored (a later snapshot adds them). */
export function applyStatusChange(
  cards: Record<string, LiveDeviceCard>,
  change: DeviceStatusChange,
): Record<string, LiveDeviceCard> {
  const existing = cards[change.deviceId];
  if (!existing || existing.status === change.status) return cards;
  return { ...cards, [change.deviceId]: { ...existing, status: change.status } };
}

/** Prepend `item` (newest first), drop duplicates by key and cap the list. */
export function prependCapped<T>(list: T[], item: T, key: (t: T) => string, limit: number): T[] {
  const k = key(item);
  const next = [item, ...list.filter((x) => key(x) !== k)];
  return next.length > limit ? next.slice(0, limit) : next;
}

// ----- Store ------------------------------------------------------------------------------------

interface LiveStore {
  cards: Record<string, LiveDeviceCard>;
  /** true once a snapshot (GET /live) has been merged. */
  loaded: boolean;
  feed: EventDto[];
  alerts: SecurityEventDto[];
  hubState: HubState;
  /** Incremented after every (re)connect so pages can refetch a snapshot to close any gap. */
  resync: number;

  setSnapshot: (cards: LiveDeviceCard[]) => void;
  upsertCard: (card: LiveDeviceCard) => void;
  applyStatusChange: (change: DeviceStatusChange) => void;
  pushEvent: (event: EventDto) => void;
  pushAlert: (alert: SecurityEventDto) => void;
  setHubState: (state: HubState) => void;
  bumpResync: () => void;
  clearFeed: () => void;
  reset: () => void;
}

export const useLiveStore = create<LiveStore>((set) => ({
  cards: {},
  loaded: false,
  feed: [],
  alerts: [],
  hubState: "idle",
  resync: 0,

  setSnapshot: (cards) => set({ cards: indexCards(cards), loaded: true }),
  upsertCard: (card) => set((s) => ({ cards: { ...s.cards, [card.deviceId]: card } })),
  applyStatusChange: (change) => set((s) => ({ cards: applyStatusChange(s.cards, change) })),
  pushEvent: (event) => set((s) => ({ feed: prependCapped(s.feed, event, (e) => e.eventId, FEED_LIMIT) })),
  pushAlert: (alert) => set((s) => ({ alerts: prependCapped(s.alerts, alert, (a) => a.id, ALERT_LIMIT) })),
  setHubState: (hubState) => set({ hubState }),
  bumpResync: () => set((s) => ({ resync: s.resync + 1 })),
  clearFeed: () => set({ feed: [] }),
  reset: () => set({ cards: {}, loaded: false, feed: [], alerts: [], hubState: "idle" }),
}));
