"use client";

import { useCallback, useMemo, useState } from "react";
import type { FilterState } from "@/lib/filters";

export interface FiltersHandle {
  filters: FilterState;
  setFilters: (next: FilterState) => void;
  clear: () => void;
  /** Number of active (non-empty) filters. */
  activeCount: number;
}

export function useFilters(initial: FilterState = {}): FiltersHandle {
  const [filters, setFiltersState] = useState<FilterState>(initial);
  const setFilters = useCallback((next: FilterState) => setFiltersState(next), []);
  const clear = useCallback(() => setFiltersState({}), []);
  const activeCount = useMemo(() => Object.values(filters).filter((v) => v && v.trim()).length, [filters]);
  return { filters, setFilters, clear, activeCount };
}
