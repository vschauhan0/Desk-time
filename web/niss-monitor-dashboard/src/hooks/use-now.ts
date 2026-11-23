"use client";

import { useSyncExternalStore } from "react";

/** One shared 1 Hz ticker for every "8 seconds ago" label on the page. */
const listeners = new Set<() => void>();
let timer: ReturnType<typeof setInterval> | null = null;
let snapshot = 0;

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (timer === null) {
    snapshot = Date.now();
    timer = setInterval(() => {
      snapshot = Date.now();
      listeners.forEach((l) => l());
    }, 1000);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  };
}

const INITIAL_NOW = Date.now();

function getSnapshot(): number {
  if (snapshot === 0) snapshot = Date.now();
  return snapshot;
}

/** Current time in ms, re-rendering the caller once per second. */
export function useNow(): number {
  return useSyncExternalStore(subscribe, getSnapshot, () => INITIAL_NOW);
}
