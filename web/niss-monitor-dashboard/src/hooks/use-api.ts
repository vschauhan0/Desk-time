"use client";

import useSWR, { type SWRConfiguration, type KeyedMutator } from "swr";
import { api, buildPath, type Query } from "@/lib/api";

export interface ApiState<T> {
  data: T | undefined;
  error: Error | undefined;
  /** true only for the very first load (no data yet). */
  loading: boolean;
  /** true while any request (incl. background revalidation) is in flight. */
  validating: boolean;
  mutate: KeyedMutator<T>;
}

export interface UseApiOptions {
  /** Poll interval in ms (0 = none). */
  refreshInterval?: number;
  /** Set false to skip fetching (e.g. until a filter is chosen). */
  enabled?: boolean;
  keepPrevious?: boolean;
}

/** Typed GET with SWR caching. `path` must be relative to /api/v1 (e.g. "/devices"). */
export function useApi<T>(path: string | null, query?: Query, options: UseApiOptions = {}): ApiState<T> {
  const { refreshInterval = 0, enabled = true, keepPrevious = true } = options;
  const key = path && enabled ? buildPath(path, query) : null;
  const config: SWRConfiguration<T, Error> = {
    refreshInterval,
    keepPreviousData: keepPrevious,
    revalidateOnFocus: false,
    shouldRetryOnError: false,
    dedupingInterval: 2000,
  };
  const { data, error, isLoading, isValidating, mutate } = useSWR<T, Error>(
    key,
    (k: string) => api.get<T>(k),
    config,
  );
  return { data, error, loading: isLoading, validating: isValidating, mutate };
}
