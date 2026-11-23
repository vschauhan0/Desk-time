"use client";

import { useMemo, useState } from "react";
import {
  defaultCustomRange,
  rangeToQuery,
  resolveDateRange,
  type CustomRange,
  type DatePreset,
  type DateRange,
} from "@/lib/filters";
import type { Query } from "@/lib/api";

export interface DateRangeState {
  preset: DatePreset;
  custom: CustomRange;
  range: DateRange;
  /** Stable `from`/`to`/`tzOffsetMinutes` query object. */
  query: Query;
  setPreset: (p: DatePreset) => void;
  setCustom: (c: CustomRange) => void;
}

/**
 * Date-range state shared by FilterBar-driven pages. The resolved range is memoised on the preset/custom
 * values (and the calendar day), so the query key stays stable between renders and SWR does not refetch.
 */
export function useDateRange(initial: DatePreset = "today"): DateRangeState {
  const [preset, setPreset] = useState<DatePreset>(initial);
  const [custom, setCustom] = useState<CustomRange>(() => defaultCustomRange());
  const dayKey = new Date().toDateString();

  return useMemo(() => {
    const range = resolveDateRange(preset, custom);
    return { preset, custom, range, query: rangeToQuery(range), setPreset, setCustom };
    // dayKey: recompute when local midnight passes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preset, custom, dayKey]);
}
