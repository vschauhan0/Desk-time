"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, type ReactNode } from "react";
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useSWRConfig } from "swr";
import { getValidAccessToken } from "@/lib/api";
import { getApiBaseUrl } from "@/lib/config";
import { HubMethods } from "@/lib/types";
import type { DeviceStatusChange, EventDto, LiveDeviceCard, SecurityEventDto } from "@/lib/types";
import { useLiveStore } from "@/store/live-store";
import { useAuth } from "./auth-provider";

interface MonitorHub {
  /** Join the "feed" group (all non-metric events). Returns the unsubscribe function. Ref-counted. */
  subscribeFeed: () => () => void;
  /** Join group device:{id}. Returns the unsubscribe function. Ref-counted. */
  subscribeDevice: (deviceId: string) => () => void;
}

const HubContext = createContext<MonitorHub | null>(null);

const RETRY_CAP_MS = 30_000;

/** Reconnect delay policy: 0.5s, 1s, 2s ... capped at 30s, never gives up. */
export function reconnectDelay(previousRetryCount: number): number {
  return Math.min(RETRY_CAP_MS, 500 * 2 ** Math.min(previousRetryCount, 10));
}

/**
 * One shared SignalR connection for the whole authenticated app. It feeds the zustand live store;
 * pages subscribe to groups through the context and read state from the store.
 */
export function SignalRProvider({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const { mutate } = useSWRConfig();
  const connRef = useRef<HubConnection | null>(null);
  const feedCount = useRef(0);
  const deviceCounts = useRef(new Map<string, number>());

  const invokeSafe = useCallback((method: string, ...args: string[]) => {
    const conn = connRef.current;
    if (conn?.state === HubConnectionState.Connected) {
      conn.invoke(method, ...args).catch(() => undefined);
    }
  }, []);

  const resubscribeAll = useCallback(() => {
    if (feedCount.current > 0) invokeSafe("SubscribeFeed");
    for (const id of deviceCounts.current.keys()) invokeSafe("SubscribeDevice", id);
  }, [invokeSafe]);

  useEffect(() => {
    if (status !== "authenticated") return;
    let cancelled = false;
    useLiveStore.getState().setHubState("connecting");

    let refreshTimer: ReturnType<typeof setTimeout> | null = null;
    const refreshOverview = () => {
      if (refreshTimer) return;
      refreshTimer = setTimeout(() => {
        refreshTimer = null;
        void mutate(
          (key) => typeof key === "string" && (key.startsWith("/overview") || key.startsWith("/security-events")),
        );
      }, 1500);
    };

    void (async () => {
      const base = await getApiBaseUrl();
      if (cancelled) return;
      const conn = new HubConnectionBuilder()
        .withUrl(`${base}${HubMethods.Route}`, {
          accessTokenFactory: async () => (await getValidAccessToken()) ?? "",
          // Bearer tokens, not cookies: no credentialed CORS needed on the API.
          withCredentials: false,
        })
        .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => reconnectDelay(ctx.previousRetryCount) })
        .configureLogging(LogLevel.Warning)
        .build();
      connRef.current = conn;

      conn.on(HubMethods.DeviceStateChanged, (card: LiveDeviceCard) => useLiveStore.getState().upsertCard(card));
      conn.on(HubMethods.DeviceStatusChanged, (change: DeviceStatusChange) => {
        useLiveStore.getState().applyStatusChange(change);
        refreshOverview();
      });
      conn.on(HubMethods.EventIngested, (event: EventDto) => useLiveStore.getState().pushEvent(event));
      conn.on(HubMethods.SecurityEventRaised, (alert: SecurityEventDto) => {
        useLiveStore.getState().pushAlert(alert);
        refreshOverview();
      });

      conn.onreconnecting(() => useLiveStore.getState().setHubState("reconnecting"));
      conn.onreconnected(() => {
        const s = useLiveStore.getState();
        s.setHubState("connected");
        resubscribeAll();
        s.bumpResync();
      });

      // Initial connect (withAutomaticReconnect does not retry a failed first start) and full restart after close.
      const startLoop = async () => {
        let attempt = 0;
        while (!cancelled) {
          try {
            useLiveStore.getState().setHubState(attempt === 0 ? "connecting" : "reconnecting");
            await conn.start();
            if (cancelled) return;
            const s = useLiveStore.getState();
            s.setHubState("connected");
            resubscribeAll();
            s.bumpResync();
            return;
          } catch {
            attempt += 1;
            await new Promise((r) => setTimeout(r, reconnectDelay(attempt)));
          }
        }
      };
      conn.onclose(() => {
        if (cancelled) return;
        useLiveStore.getState().setHubState("disconnected");
        void startLoop();
      });
      void startLoop();
    })();

    return () => {
      cancelled = true;
      if (refreshTimer) clearTimeout(refreshTimer);
      const conn = connRef.current;
      connRef.current = null;
      if (conn) void conn.stop();
      useLiveStore.getState().reset();
    };
  }, [status, mutate, resubscribeAll]);

  const subscribeFeed = useCallback(() => {
    feedCount.current += 1;
    if (feedCount.current === 1) invokeSafe("SubscribeFeed");
    return () => {
      feedCount.current = Math.max(0, feedCount.current - 1);
      if (feedCount.current === 0) invokeSafe("UnsubscribeFeed");
    };
  }, [invokeSafe]);

  const subscribeDevice = useCallback(
    (deviceId: string) => {
      const map = deviceCounts.current;
      const n = (map.get(deviceId) ?? 0) + 1;
      map.set(deviceId, n);
      if (n === 1) invokeSafe("SubscribeDevice", deviceId);
      return () => {
        const left = (map.get(deviceId) ?? 1) - 1;
        if (left <= 0) {
          map.delete(deviceId);
          invokeSafe("UnsubscribeDevice", deviceId);
        } else {
          map.set(deviceId, left);
        }
      };
    },
    [invokeSafe],
  );

  const value = useMemo<MonitorHub>(() => ({ subscribeFeed, subscribeDevice }), [subscribeFeed, subscribeDevice]);
  return <HubContext.Provider value={value}>{children}</HubContext.Provider>;
}

export function useMonitorHub(): MonitorHub {
  const ctx = useContext(HubContext);
  if (!ctx) throw new Error("useMonitorHub must be used inside <SignalRProvider>");
  return ctx;
}
