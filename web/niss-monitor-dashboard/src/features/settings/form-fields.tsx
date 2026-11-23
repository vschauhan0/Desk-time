"use client";

import type { ReactNode } from "react";

export function NumberField({ label, value, onChange, min = 0, max, hint, unit }: { label: string; value: number; onChange: (n: number) => void; min?: number; max?: number; hint?: ReactNode; unit?: string }) {
  return (
    <label className="block min-w-0">
      <span className="label">{label}{unit ? ` (${unit})` : ""}</span>
      <input
        className="field"
        type="number"
        min={min}
        max={max}
        value={Number.isFinite(value) ? value : 0}
        onChange={(e) => {
          const n = Number.parseInt(e.target.value, 10);
          onChange(Number.isNaN(n) ? 0 : n);
        }}
      />
      {hint && <span className="mt-1 block text-[11.5px] text-faint">{hint}</span>}
    </label>
  );
}

export function ToggleField({ label, checked, onChange, hint, warn }: { label: string; checked: boolean; onChange: (v: boolean) => void; hint?: ReactNode; warn?: boolean }) {
  return (
    <label className="flex cursor-pointer items-start gap-2.5 rounded-lg border border-line p-3">
      <input type="checkbox" className="mt-0.5 size-4 accent-[var(--brand)]" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className="min-w-0">
        <span className="block text-[13px] font-medium">{label}</span>
        {hint && <span className={`mt-0.5 block text-[11.5px] ${warn && checked ? "text-warn" : "text-faint"}`}>{hint}</span>}
      </span>
    </label>
  );
}

/** One value per line. */
export function ListField({ label, value, onChange, hint, placeholder }: { label: string; value: string[]; onChange: (v: string[]) => void; hint?: ReactNode; placeholder?: string }) {
  return (
    <label className="block min-w-0">
      <span className="label">{label}</span>
      <textarea
        className="field mono"
        rows={4}
        placeholder={placeholder}
        value={value.join("\n")}
        onChange={(e) => onChange(e.target.value.split("\n").map((l) => l.trim()).filter(Boolean))}
      />
      {hint && <span className="mt-1 block text-[11.5px] text-faint">{hint}</span>}
    </label>
  );
}

export function SectionTitle({ children }: { children: ReactNode }) {
  return <h3 className="col-span-full mt-2 border-b border-line pb-1 text-[12px] font-semibold uppercase tracking-wider text-muted">{children}</h3>;
}
