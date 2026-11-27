"use client";

import clsx from "clsx";
import {
  AlertTriangle,
  CheckCircle2,
  Circle,
  Info,
  Loader2,
  OctagonAlert,
  type LucideIcon,
} from "lucide-react";
import type { ButtonHTMLAttributes, ReactNode } from "react";
import { humanize } from "@/lib/format";
import type { Tone } from "@/lib/status";

// ----- Button -----------------------------------------------------------------------------------

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "default" | "primary" | "danger" | "ghost";
  size?: "md" | "sm";
  icon?: LucideIcon;
  loading?: boolean;
  iconOnly?: boolean;
}

export function Button({
  variant = "default",
  size = "md",
  icon: Icon,
  loading,
  iconOnly,
  className,
  children,
  disabled,
  type = "button",
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      disabled={disabled || loading}
      className={clsx(
        "btn",
        variant === "primary" && "btn-primary",
        variant === "danger" && "btn-danger",
        variant === "ghost" && "btn-ghost",
        size === "sm" && "btn-sm",
        iconOnly && "btn-icon",
        className,
      )}
      {...rest}
    >
      {loading ? <Loader2 size={14} className="animate-spin" aria-hidden /> : Icon ? <Icon size={14} aria-hidden /> : null}
      {children}
    </button>
  );
}

// ----- Pills -------------------------------------------------------------------------------------

const TONE_CLASS: Record<Tone, string> = {
  ok: "pill-ok",
  warn: "pill-warn",
  danger: "pill-danger",
  info: "pill-info",
  neutral: "",
  brand: "pill-brand",
};

const TONE_ICON: Record<Tone, LucideIcon> = {
  ok: CheckCircle2,
  warn: AlertTriangle,
  danger: OctagonAlert,
  info: Info,
  neutral: Circle,
  brand: Info,
};

export function Pill({ tone = "neutral", children, icon = true, title }: { tone?: Tone; children: ReactNode; icon?: boolean; title?: string }) {
  const Icon = TONE_ICON[tone];
  return (
    <span className={clsx("pill", TONE_CLASS[tone])} title={title}>
      {icon && <Icon size={12} aria-hidden />}
      {children}
    </span>
  );
}

/** Status pill: humanised label + icon. */
export function StatusPill({ value, tone }: { value: string | null | undefined; tone: Tone }) {
  return <Pill tone={tone}>{humanize(value)}</Pill>;
}

/** The three separate application concepts (see docs/monitoring-events.md). */
export function AppStateBadges({ running, foreground, interaction }: { running: boolean; foreground: boolean; interaction: boolean }) {
  return (
    <span className="flex flex-wrap gap-1">
      <span
        className={clsx("pill", running ? "pill-info" : "opacity-40")}
        title="RUNNING: the process exists (open), regardless of whether anyone is using it"
      >
        Running
      </span>
      <span
        className={clsx("pill", foreground ? "pill-brand" : "opacity-40")}
        title="FOREGROUND: the application owns the foreground window"
      >
        Foreground
      </span>
      <span
        className={clsx("pill", interaction ? "pill-ok" : "opacity-40")}
        title="INTERACTING: mouse/keyboard input within the interaction window"
      >
        Interacting
      </span>
    </span>
  );
}

// ----- Layout bits -------------------------------------------------------------------------------

export function PageHeader({ title, subtitle, actions }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
        {subtitle && <p className="mt-0.5 max-w-3xl text-[13px] text-muted">{subtitle}</p>}
      </div>
      {actions && <div className="no-print flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

export function Card({ title, actions, children, className, padded = true, fill = false, bodyClassName }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; padded?: boolean; fill?: boolean; bodyClassName?: string }) {
  // fill: the card becomes a flex column whose body grows to the card's height (set by the parent grid/flex cell),
  // so a chart inside can size to the remaining space. Used by the viewport-fit dashboard.
  return (
    <section className={clsx("card min-w-0", fill && "flex h-full flex-col", className)}>
      {(title || actions) && (
        <header className="flex shrink-0 items-center justify-between gap-3 border-b border-line px-4 py-3">
          <h2 className="truncate text-[13px] font-semibold">{title}</h2>
          {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={clsx(padded && "p-4", fill && "min-h-0 flex-1", bodyClassName)}>{children}</div>
    </section>
  );
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={clsx("skeleton", className)} aria-hidden />;
}

export function Notice({ tone = "info", children }: { tone?: Tone; children: ReactNode }) {
  const Icon = TONE_ICON[tone];
  const color =
    tone === "danger"
      ? "border-danger/40 bg-danger-soft text-danger"
      : tone === "warn"
        ? "border-warn/40 bg-warn-soft text-warn"
        : tone === "ok"
          ? "border-ok/40 bg-ok-soft text-ok"
          : "border-info/40 bg-info-soft text-info";
  return (
    <div role={tone === "danger" ? "alert" : "status"} className={clsx("flex items-start gap-2 rounded-lg border px-3 py-2 text-[13px]", color)}>
      <Icon size={15} className="mt-0.5 shrink-0" aria-hidden />
      <div className="min-w-0 text-fg">{children}</div>
    </div>
  );
}

export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return (
    <label className="block min-w-0">
      <span className="label">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[11.5px] text-faint">{hint}</span>}
    </label>
  );
}
