"use client";

import { FilterX } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { useApi } from "@/hooks/use-api";
import type { DateRangeState } from "@/hooks/use-date-range";
import { DATE_PRESETS, type DatePreset, type FilterKey, type FilterState } from "@/lib/filters";
import { humanize } from "@/lib/format";
import { APP_CATEGORIES, EVENT_TYPES } from "@/lib/types";
import type { DeviceSummary, EmployeeDto, Paged } from "@/lib/types";
import { Button } from "@/components/ui/primitives";

const LABELS: Record<FilterKey, string> = {
  employee: "Employee",
  department: "Department",
  device: "Device",
  application: "Application",
  process: "Process",
  eventType: "Event type",
  status: "Status",
  category: "Category",
  search: "Search",
};

export interface FilterBarProps {
  fields: readonly FilterKey[];
  value: FilterState;
  onChange: (next: FilterState) => void;
  /** Date range controls (Today / Yesterday / Last 7 days / Custom). Omit for pages without a range. */
  dateRange?: DateRangeState;
  /** Options of the Status select. */
  statusOptions?: readonly string[];
  /** Placeholder of the free-text `search` field. */
  searchPlaceholder?: string;
  extra?: ReactNode;
}

/** Text input that commits to the parent 300 ms after typing stops. */
function DebouncedInput({ value, onCommit, placeholder, label }: { value: string; onCommit: (v: string) => void; placeholder?: string; label: string }) {
  const [text, setText] = useState(value);
  useEffect(() => {
    if (text === value) return;
    const t = setTimeout(() => onCommit(text), 300);
    return () => clearTimeout(t);
  }, [text, value, onCommit]);
  return <input className="field" type="search" value={text} placeholder={placeholder} aria-label={label} onChange={(e) => setText(e.target.value)} />;
}

function Select({ label, value, onChange, options, allLabel }: { label: string; value: string; onChange: (v: string) => void; options: { value: string; label: string }[]; allLabel: string }) {
  return (
    <select className="field" aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{allLabel}</option>
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

/**
 * Reusable filter row for Employee, Department, Device, Application, Process, Event type, Status, Category, text
 * search and the date-range presets. Option lists (departments, employees, devices) are loaded lazily from the API
 * only when the corresponding field is requested.
 */
export function FilterBar({ fields, value, onChange, dateRange, statusOptions = [], searchPlaceholder = "Search...", extra }: FilterBarProps) {
  const has = (k: FilterKey) => fields.includes(k);
  const departments = useApi<string[]>(has("department") ? "/departments" : null);
  const employees = useApi<Paged<EmployeeDto>>(has("employee") ? "/employees" : null, { pageSize: 500 });
  const devices = useApi<Paged<DeviceSummary>>(has("device") ? "/devices" : null, { pageSize: 500 });
  const [resetKey, setResetKey] = useState(0);

  const set = (key: FilterKey) => (v: string) => onChange({ ...value, [key]: v || undefined });
  const dirty = Object.values(value).some((v) => v && v.trim());

  return (
    <div className="no-print card mb-4 flex flex-wrap items-end gap-3 p-3" role="search" aria-label="Filters">
      {dateRange && (
        <div className="flex flex-wrap items-end gap-2">
          <div>
            <span className="label">Period</span>
            <div className="flex overflow-hidden rounded-lg border border-line-strong" role="group" aria-label="Date range">
              {DATE_PRESETS.map((p) => (
                <button
                  key={p.value}
                  type="button"
                  aria-pressed={dateRange.preset === p.value}
                  onClick={() => dateRange.setPreset(p.value as DatePreset)}
                  className={`h-[34px] px-3 text-[13px] font-medium ${dateRange.preset === p.value ? "bg-brand text-on-brand" : "bg-surface text-muted hover:bg-surface-2"}`}
                >
                  {p.label}
                </button>
              ))}
            </div>
          </div>
          {dateRange.preset === "custom" && (
            <>
              <div>
                <span className="label">From</span>
                <input className="field" type="date" value={dateRange.custom.from} max={dateRange.custom.to} onChange={(e) => dateRange.setCustom({ ...dateRange.custom, from: e.target.value })} aria-label="From date" />
              </div>
              <div>
                <span className="label">To</span>
                <input className="field" type="date" value={dateRange.custom.to} min={dateRange.custom.from} onChange={(e) => dateRange.setCustom({ ...dateRange.custom, to: e.target.value })} aria-label="To date" />
              </div>
            </>
          )}
        </div>
      )}

      {has("employee") && (
        <div className="w-44">
          <span className="label">{LABELS.employee}</span>
          <Select
            label="Employee"
            allLabel="All employees"
            value={value.employee ?? ""}
            onChange={set("employee")}
            options={(employees.data?.items ?? []).map((e) => ({ value: e.employeeCode, label: `${e.fullName} (${e.employeeCode})` }))}
          />
        </div>
      )}
      {has("department") && (
        <div className="w-40">
          <span className="label">{LABELS.department}</span>
          <Select label="Department" allLabel="All departments" value={value.department ?? ""} onChange={set("department")} options={(departments.data ?? []).map((d) => ({ value: d, label: d }))} />
        </div>
      )}
      {has("device") && (
        <div className="w-44">
          <span className="label">{LABELS.device}</span>
          <Select label="Device" allLabel="All devices" value={value.device ?? ""} onChange={set("device")} options={(devices.data?.items ?? []).map((d) => ({ value: d.deviceId, label: d.hostname === d.deviceId ? d.deviceId : `${d.hostname} (${d.deviceId})` }))} />
        </div>
      )}
      {has("status") && (
        <div className="w-40">
          <span className="label">{LABELS.status}</span>
          <Select label="Status" allLabel="Any status" value={value.status ?? ""} onChange={set("status")} options={statusOptions.map((s) => ({ value: s, label: humanize(s) }))} />
        </div>
      )}
      {has("category") && (
        <div className="w-40">
          <span className="label">{LABELS.category}</span>
          <Select label="Category" allLabel="All categories" value={value.category ?? ""} onChange={set("category")} options={APP_CATEGORIES.map((c) => ({ value: c, label: c }))} />
        </div>
      )}
      {has("eventType") && (
        <div className="w-52">
          <span className="label">{LABELS.eventType}</span>
          <Select label="Event type" allLabel="All event types" value={value.eventType ?? ""} onChange={set("eventType")} options={EVENT_TYPES.map((t) => ({ value: t, label: humanize(t) }))} />
        </div>
      )}
      {has("application") && (
        <div className="w-40">
          <span className="label">{LABELS.application}</span>
          <DebouncedInput key={`app-${resetKey}`} label="Application" placeholder="e.g. chrome.exe" value={value.application ?? ""} onCommit={set("application")} />
        </div>
      )}
      {has("process") && (
        <div className="w-40">
          <span className="label">{LABELS.process}</span>
          <DebouncedInput key={`proc-${resetKey}`} label="Process" placeholder="e.g. svchost.exe" value={value.process ?? ""} onCommit={set("process")} />
        </div>
      )}
      {has("search") && (
        <div className="w-56 grow sm:max-w-xs">
          <span className="label">{LABELS.search}</span>
          <DebouncedInput key={`search-${resetKey}`} label="Search" placeholder={searchPlaceholder} value={value.search ?? ""} onCommit={set("search")} />
        </div>
      )}

      {extra}

      {dirty && (
        <Button
          icon={FilterX}
          onClick={() => {
            onChange({});
            setResetKey((k) => k + 1);
          }}
        >
          Clear
        </Button>
      )}
    </div>
  );
}
