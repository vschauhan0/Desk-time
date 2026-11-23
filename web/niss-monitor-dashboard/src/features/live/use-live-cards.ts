"use client";

import { useEffect, useMemo } from "react";
import { useApi } from "@/hooks/use-api";
import type { LiveDeviceCard } from "@/lib/types";
import { useLiveStore } from "@/store/live-store";

/**
 * Live device cards: REST snapshot (GET /live) seeds the zustand store, SignalR pushes patch it in place, and every
 * (re)connect triggers a fresh snapshot so nothing is missed while the socket was down.
 */
export function useLiveCards() {
  const snapshot = useApi<LiveDeviceCard[]>("/live");
  const setSnapshot = useLiveStore((s) => s.setSnapshot);
  const cardsMap = useLiveStore((s) => s.cards);
  const loaded = useLiveStore((s) => s.loaded);
  const resync = useLiveStore((s) => s.resync);
  const { mutate, data } = snapshot;

  useEffect(() => {
    if (data) setSnapshot(data);
  }, [data, setSnapshot]);

  useEffect(() => {
    if (resync > 0) void mutate();
  }, [resync, mutate]);

  const cards = useMemo(() => Object.values(cardsMap), [cardsMap]);
  return { cards, loaded, loading: snapshot.loading && !loaded, error: loaded ? undefined : snapshot.error, mutate };
}
