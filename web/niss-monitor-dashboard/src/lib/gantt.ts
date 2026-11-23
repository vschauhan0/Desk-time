import type { ProcessSessionDto } from "./types";

export interface GanttBar {
  session: ProcessSessionDto;
  /** 0..100 */
  leftPct: number;
  /** 0..100, at least `minWidthPct` so very short processes stay visible */
  widthPct: number;
  /** true when the process has no end time (still RUNNING): bar extends to "now" / end of range */
  open: boolean;
}

export interface GanttLayout {
  fromMs: number;
  toMs: number;
  bars: GanttBar[];
  ticks: { pct: number; ms: number }[];
}

/**
 * Lay out process lifetimes on a shared time axis. The axis spans [rangeFrom, rangeTo] clipped to the data:
 * from the earliest start (or rangeFrom) to the latest end/now (or rangeTo).
 */
export function layoutGantt(
  sessions: ProcessSessionDto[],
  rangeFromMs: number,
  rangeToMs: number,
  nowMs: number,
  tickCount = 6,
  minWidthPct = 0.4,
): GanttLayout {
  const clippedEnd = Math.min(rangeToMs, Math.max(nowMs, rangeFromMs));
  const ends = sessions.map((s) => (s.endedAtUtc ? Date.parse(s.endedAtUtc) : clippedEnd));
  const starts = sessions.map((s) => Date.parse(s.startedAtUtc));
  const fromMs = starts.length ? Math.max(rangeFromMs, Math.min(...starts)) : rangeFromMs;
  const toMs = ends.length ? Math.min(rangeToMs, Math.max(...ends)) : clippedEnd;
  const span = Math.max(1, toMs - fromMs);

  const bars = sessions.map((s, i): GanttBar => {
    const start = Math.max(fromMs, starts[i]);
    const end = Math.min(toMs, ends[i]);
    const leftPct = ((start - fromMs) / span) * 100;
    const widthPct = Math.max(minWidthPct, ((Math.max(start, end) - start) / span) * 100);
    return { session: s, leftPct, widthPct: Math.min(widthPct, 100 - leftPct), open: !s.endedAtUtc };
  });

  const ticks = Array.from({ length: tickCount }, (_, i) => {
    const pct = (i / (tickCount - 1)) * 100;
    return { pct, ms: fromMs + (span * pct) / 100 };
  });
  return { fromMs, toMs, bars, ticks };
}
