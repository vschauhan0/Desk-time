"use client";

import type { ReactNode } from "react";
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  Line,
  LineChart,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type TooltipContentProps,
} from "recharts";
import { formatDateTime, formatDuration, formatUtc } from "@/lib/format";

/** Fixed categorical order (validated palette): blue, orange, aqua, yellow, magenta, green, violet, red. */
export const SERIES_COLORS = ["var(--c1)", "var(--c2)", "var(--c3)", "var(--c4)", "var(--c5)", "var(--c6)", "var(--c7)", "var(--c8)"] as const;

/** Presence colours are stable across every chart: never re-assigned by rank. */
export const PRESENCE_COLORS = { active: "var(--c1)", idle: "var(--c2)", locked: "var(--c3)" } as const;
/** Application concepts: running (open) / foreground / actively used (interaction). Distinct from presence colours so both can share a page. */
export const APP_CONCEPT_COLORS = { running: "var(--c7)", foreground: "var(--c5)", interaction: "var(--c6)" } as const;

export interface SeriesDef {
  key: string;
  label: string;
  color: string;
}

type TooltipProps = TooltipContentProps;

function ChartTooltip({ active, payload, label, format, labelFormat }: TooltipProps & { format: (n: number) => string; labelFormat?: (l: unknown) => string }) {
  if (!active || !payload || payload.length === 0) return null;
  return (
    <div className="rounded-lg border border-line bg-surface px-3 py-2 text-[12px]" style={{ boxShadow: "var(--shadow-pop)" }}>
      <p className="mb-1 font-semibold">{labelFormat ? labelFormat(label) : String(label ?? "")}</p>
      {payload.map((p) => (
        <p key={String(p.dataKey)} className="flex items-center gap-2 text-muted">
          <span className="inline-block size-2 rounded-sm" style={{ background: p.color ?? p.fill }} aria-hidden />
          <span>{p.name}</span>
          <span className="ml-auto pl-3 font-medium tabular-nums text-fg">{format(Number(p.value ?? 0))}</span>
        </p>
      ))}
    </div>
  );
}

function ChartFrame({ height, children, label }: { height: number | "fill"; children: ReactNode; label: string }) {
  const fill = height === "fill";
  return (
    <div role="img" aria-label={label} style={fill ? undefined : { height }} className={fill ? "h-full min-h-0 w-full min-w-0" : "w-full min-w-0"}>
      <ResponsiveContainer width="100%" height="100%">
        {children as React.ReactElement}
      </ResponsiveContainer>
    </div>
  );
}

const legendStyle = { fontSize: 12, paddingTop: 6 } as const;

/** Stacked hourly area chart (seconds in, minutes on the axis). Pass height="fill" to size to a flex parent. */
export function StackedAreaByHour({ data, series, height = 260 }: { data: Record<string, number>[]; series: SeriesDef[]; height?: number | "fill" }) {
  return (
    <ChartFrame height={height} label="Activity by hour">
      <AreaChart data={data} margin={{ top: 8, right: 8, left: -12, bottom: 0 }}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="hour" tickFormatter={(h: number) => `${String(h).padStart(2, "0")}h`} tickLine={false} axisLine={false} interval={2} />
        <YAxis tickFormatter={(s: number) => formatDuration(s)} tickLine={false} axisLine={false} width={56} />
        <Tooltip content={(p) => <ChartTooltip {...p} format={(n) => formatDuration(n)} labelFormat={(l) => `${String(l).padStart(2, "0")}:00`} />} />
        <Legend wrapperStyle={legendStyle} iconType="square" />
        {series.map((s) => (
          <Area key={s.key} type="monotone" dataKey={s.key} name={s.label} stackId="a" stroke={s.color} fill={s.color} fillOpacity={0.35} strokeWidth={2} />
        ))}
      </AreaChart>
    </ChartFrame>
  );
}

/** Horizontal bars, one row per category, one or more series (grouped or stacked). Values are seconds. */
export function DurationBars({ data, series, stacked = false, rowHeight = 30, nameKey = "name", valueFormat = formatDuration, label }: { data: Record<string, string | number>[]; series: SeriesDef[]; stacked?: boolean; rowHeight?: number; nameKey?: string; valueFormat?: (n: number) => string; label: string }) {
  const perRow = stacked ? rowHeight : rowHeight * Math.max(1, series.length) * 0.6;
  const height = Math.max(160, data.length * perRow + 56);
  return (
    <ChartFrame height={height} label={label}>
      <BarChart data={data} layout="vertical" margin={{ top: 4, right: 16, left: 8, bottom: 0 }} barCategoryGap={stacked ? 6 : 8}>
        <CartesianGrid horizontal={false} />
        <XAxis type="number" tickFormatter={(s: number) => valueFormat(s)} tickLine={false} axisLine={false} />
        <YAxis type="category" dataKey={nameKey} width={130} tickLine={false} axisLine={false} interval={0} tickFormatter={(v: string) => (v.length > 20 ? `${v.slice(0, 19)}…` : v)} />
        <Tooltip cursor={{ fill: "var(--surface-2)" }} content={(p) => <ChartTooltip {...p} format={valueFormat} />} />
        <Legend wrapperStyle={legendStyle} iconType="square" />
        {series.map((s, i) => (
          <Bar
            key={s.key}
            dataKey={s.key}
            name={s.label}
            fill={s.color}
            stackId={stacked ? "s" : undefined}
            radius={stacked ? (i === series.length - 1 ? [0, 4, 4, 0] : 0) : [0, 4, 4, 0]}
            stroke={stacked ? "var(--surface)" : undefined}
            strokeWidth={stacked ? 2 : 0}
            maxBarSize={18}
          />
        ))}
      </BarChart>
    </ChartFrame>
  );
}

export interface DonutSlice {
  key: string;
  label: string;
  value: number;
  color: string;
}

/**
 * Donut chart with a centred total and an inline legend. Built for a fixed-height dashboard cell: the ring sizes to
 * its container, so the parent controls the height. Zero-value slices are dropped so the ring is never a sliver of noise.
 */
export function Donut({ slices, centerLabel, height = 180 }: { slices: DonutSlice[]; centerLabel?: string; height?: number }) {
  const data = slices.filter((s) => s.value > 0);
  const total = slices.reduce((n, s) => n + s.value, 0);
  return (
    <div className="flex min-w-0 items-center gap-4">
      <div className="relative shrink-0" style={{ width: height, height }}>
        <ResponsiveContainer width="100%" height="100%">
          <PieChart>
            <Pie data={data} dataKey="value" nameKey="label" innerRadius="64%" outerRadius="100%" paddingAngle={data.length > 1 ? 2 : 0} stroke="var(--surface)" strokeWidth={2} isAnimationActive={false}>
              {data.map((s) => (
                <Cell key={s.key} fill={s.color} />
              ))}
            </Pie>
            <Tooltip content={(p) => <ChartTooltip {...p} format={(n) => String(n)} />} />
          </PieChart>
        </ResponsiveContainer>
        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
          <span className="text-2xl font-semibold tabular-nums leading-none">{total}</span>
          {centerLabel && <span className="mt-0.5 text-[11px] text-faint">{centerLabel}</span>}
        </div>
      </div>
      <ul className="min-w-0 flex-1 space-y-1.5">
        {slices.map((s) => (
          <li key={s.key} className="flex items-center gap-2 text-[12.5px]">
            <span className="inline-block size-2.5 shrink-0 rounded-sm" style={{ background: s.color }} aria-hidden />
            <span className="min-w-0 truncate text-muted">{s.label}</span>
            <span className="ml-auto pl-2 font-medium tabular-nums">{s.value}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Percent time-series (CPU / RAM / disk). */
export function PercentLines({ data, series, height = 240 }: { data: Record<string, number | string>[]; series: SeriesDef[]; height?: number }) {
  return (
    <ChartFrame height={height} label="Utilisation over time">
      <LineChart data={data} margin={{ top: 8, right: 12, left: -16, bottom: 0 }}>
        <CartesianGrid vertical={false} />
        <XAxis
          dataKey="t"
          type="number"
          scale="time"
          domain={["dataMin", "dataMax"]}
          tickFormatter={(t: number) => new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
          tickLine={false}
          axisLine={false}
          minTickGap={40}
        />
        <YAxis domain={[0, 100]} tickFormatter={(v: number) => `${v}%`} tickLine={false} axisLine={false} width={48} />
        <Tooltip
          content={(p) => <ChartTooltip {...p} format={(n) => `${n.toFixed(1)}%`} labelFormat={(l) => `${formatDateTime(new Date(Number(l)).toISOString())}  (${formatUtc(new Date(Number(l)))})`} />}
        />
        <Legend wrapperStyle={legendStyle} iconType="plainline" />
        {series.map((s) => (
          <Line key={s.key} type="monotone" dataKey={s.key} name={s.label} stroke={s.color} strokeWidth={2} dot={false} connectNulls isAnimationActive={false} />
        ))}
      </LineChart>
    </ChartFrame>
  );
}
