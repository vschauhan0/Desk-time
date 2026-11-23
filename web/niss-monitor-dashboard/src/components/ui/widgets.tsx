"use client";

import clsx from "clsx";
import { Check, ChevronLeft, ChevronRight, Copy, X } from "lucide-react";
import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import { formatDateTime, formatDuration, formatRelative, formatUtc } from "@/lib/format";
import type { Tone } from "@/lib/status";
import { useNow } from "@/hooks/use-now";
import { Button } from "./primitives";

// ----- Time / duration ---------------------------------------------------------------------------

/** Local time with a UTC tooltip. */
export function Time({ value, className }: { value: string | null | undefined; className?: string }) {
  if (!value) return <span className="text-faint">{"—"}</span>;
  return (
    <time dateTime={value} title={formatUtc(value)} className={clsx("tabular-nums", className)}>
      {formatDateTime(value)}
    </time>
  );
}

/** "8 seconds ago" - ticks every second through the shared ticker. */
export function RelativeTime({ value, className }: { value: string | null | undefined; className?: string }) {
  const now = useNow();
  return (
    <time dateTime={value ?? undefined} title={value ? `${formatDateTime(value)} (${formatUtc(value)})` : "never"} className={clsx("tabular-nums", className)}>
      {formatRelative(value, now)}
    </time>
  );
}

export function Duration({ seconds, className }: { seconds: number | null | undefined; className?: string }) {
  return (
    <span className={clsx("tabular-nums", className)} title={seconds != null ? `${Math.round(seconds)} seconds` : undefined}>
      {formatDuration(seconds)}
    </span>
  );
}

// ----- KPI card ----------------------------------------------------------------------------------

const KPI_TONE: Record<Tone, string> = {
  ok: "text-ok bg-ok-soft",
  warn: "text-warn bg-warn-soft",
  danger: "text-danger bg-danger-soft",
  info: "text-info bg-info-soft",
  neutral: "text-muted bg-neutral-soft",
  brand: "text-brand bg-brand-soft",
};

export function KpiCard({ label, value, hint, icon: Icon, tone = "neutral" }: { label: string; value: ReactNode; hint?: ReactNode; icon?: LucideIcon; tone?: Tone }) {
  return (
    <div className="card flex items-start justify-between gap-3 p-4">
      <div className="min-w-0">
        <p className="label !mb-1">{label}</p>
        <p className="text-2xl font-semibold tabular-nums leading-tight">{value}</p>
        {hint && <p className="mt-1 truncate text-[12px] text-muted">{hint}</p>}
      </div>
      {Icon && (
        <span className={clsx("flex size-9 shrink-0 items-center justify-center rounded-lg", KPI_TONE[tone])}>
          <Icon size={18} aria-hidden />
        </span>
      )}
    </div>
  );
}

// ----- Sparkline (tiny inline SVG, no axes) --------------------------------------------------------

export function Sparkline({ values, width = 96, height = 26, color = "var(--c1)", max }: { values: number[]; width?: number; height?: number; color?: string; max?: number }) {
  if (values.length < 2) return <span className="text-faint">{"—"}</span>;
  const hi = max ?? Math.max(...values, 1);
  const lo = 0;
  const step = width / (values.length - 1);
  const pts = values.map((v, i) => {
    const y = height - 2 - ((Math.min(v, hi) - lo) / (hi - lo || 1)) * (height - 4);
    return `${(i * step).toFixed(1)},${y.toFixed(1)}`;
  });
  const area = `0,${height} ${pts.join(" ")} ${width},${height}`;
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`Trend, latest ${values[values.length - 1].toFixed(0)}%`}>
      <polygon points={area} fill={color} opacity={0.14} />
      <polyline points={pts.join(" ")} fill="none" stroke={color} strokeWidth={1.75} strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

/** Horizontal utilisation bar with the value printed next to it. */
export function MiniBar({ percent, tone }: { percent: number | null | undefined; tone?: Tone }) {
  if (percent === null || percent === undefined) return <span className="text-faint">{"—"}</span>;
  const pct = Math.max(0, Math.min(100, percent));
  const color = tone === "danger" ? "var(--danger)" : tone === "warn" ? "var(--warn)" : "var(--c1)";
  return (
    <span className="inline-flex items-center gap-2">
      <span className="h-1.5 w-14 overflow-hidden rounded-full bg-surface-3" aria-hidden>
        <span className="block h-full rounded-full" style={{ width: `${pct}%`, background: color }} />
      </span>
      <span className="w-9 text-right text-[12px] tabular-nums">{pct < 10 ? pct.toFixed(1) : Math.round(pct)}%</span>
    </span>
  );
}

// ----- Dialog (native <dialog>: focus trap + Esc for free) --------------------------------------

export function Dialog({ open, onClose, title, children, footer, width = 520 }: { open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; footer?: ReactNode; width?: number }) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (open && !el.open) el.showModal();
    if (!open && el.open) el.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      onClose={onClose}
      onClick={(e) => {
        if (e.target === ref.current) onClose();
      }}
      className="m-auto w-[calc(100%-2rem)] rounded-xl border border-line bg-surface p-0 text-fg backdrop:bg-black/50"
      style={{ maxWidth: width, boxShadow: "var(--shadow-pop)" }}
    >
      {open && (
        <div className="flex max-h-[85vh] flex-col">
          <header className="flex items-center justify-between gap-3 border-b border-line px-5 py-3.5">
            <h2 id={titleId} className="text-[15px] font-semibold">
              {title}
            </h2>
            <Button variant="ghost" size="sm" iconOnly icon={X} onClick={onClose} aria-label="Close dialog" />
          </header>
          <div className="overflow-auto px-5 py-4">{children}</div>
          {footer && <footer className="flex justify-end gap-2 border-t border-line px-5 py-3">{footer}</footer>}
        </div>
      )}
    </dialog>
  );
}

export function ConfirmDialog({ open, title, message, confirmLabel = "Confirm", danger, loading, error, onConfirm, onClose }: { open: boolean; title: string; message: ReactNode; confirmLabel?: string; danger?: boolean; loading?: boolean; error?: string | null; onConfirm: () => void; onClose: () => void }) {
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      width={460}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant={danger ? "danger" : "primary"} loading={loading} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="text-[13px] text-muted">{message}</div>
      {error && <p className="mt-3 text-[13px] text-danger">{error}</p>}
    </Dialog>
  );
}

// ----- Copy field --------------------------------------------------------------------------------

export function CopyField({ value, label }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  const copy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(value);
    } catch {
      // Clipboard API needs a secure context; fall back to a temporary textarea.
      const ta = document.createElement("textarea");
      ta.value = value;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand("copy");
      ta.remove();
    }
    setCopied(true);
    setTimeout(() => setCopied(false), 1800);
  }, [value]);
  return (
    <div>
      {label && <span className="label">{label}</span>}
      <div className="flex gap-2">
        <input className="field mono" readOnly value={value} onFocus={(e) => e.currentTarget.select()} aria-label={label ?? "Value"} />
        <Button icon={copied ? Check : Copy} onClick={() => void copy()}>
          {copied ? "Copied" : "Copy"}
        </Button>
      </div>
    </div>
  );
}

// ----- Tabs --------------------------------------------------------------------------------------

export interface TabDef<K extends string> {
  key: K;
  label: string;
}

export function Tabs<K extends string>({ tabs, value, onChange }: { tabs: TabDef<K>[]; value: K; onChange: (k: K) => void }) {
  return (
    <div role="tablist" className="mb-4 flex gap-1 overflow-x-auto border-b border-line">
      {tabs.map((t) => (
        <button
          key={t.key}
          role="tab"
          type="button"
          aria-selected={value === t.key}
          onClick={() => onChange(t.key)}
          className={clsx(
            "-mb-px whitespace-nowrap border-b-2 px-3.5 py-2 text-[13px] font-medium",
            value === t.key ? "border-brand text-brand" : "border-transparent text-muted hover:text-fg",
          )}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}

// ----- Pagination --------------------------------------------------------------------------------

export function Pagination({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <div className="no-print mt-3 flex items-center justify-between gap-3 text-[12.5px] text-muted">
      <span>
        {from}-{to} of {total.toLocaleString("en-US")}
      </span>
      <div className="flex items-center gap-1">
        <Button size="sm" iconOnly icon={ChevronLeft} disabled={page <= 1} onClick={() => onPage(page - 1)} aria-label="Previous page" />
        <span className="px-2 tabular-nums">
          Page {page} / {pages}
        </span>
        <Button size="sm" iconOnly icon={ChevronRight} disabled={page >= pages} onClick={() => onPage(page + 1)} aria-label="Next page" />
      </div>
    </div>
  );
}
