"use client";

import clsx from "clsx";
import { AppWindow, Circle, Cpu, FileText, LogIn, LogOut, Lock, Moon, Server, Unlock, Usb, Wifi, type LucideIcon } from "lucide-react";
import { useMemo, useState } from "react";
import { EmptyState } from "@/components/ui/states";
import { Duration } from "@/components/ui/widgets";
import { formatDate, formatTime, formatUtc, humanize } from "@/lib/format";
import type { TimelineItem } from "@/lib/types";

interface KindStyle {
  icon: LucideIcon;
  color: string;
}

/** Icon + colour for a timeline item, from its `kind` (display grouping) with an eventType fallback. */
export function kindStyle(item: Pick<TimelineItem, "kind" | "eventType">): KindStyle {
  const k = `${item.kind} ${item.eventType}`.toUpperCase();
  if (k.includes("LOGOUT")) return { icon: LogOut, color: "var(--muted)" };
  if (k.includes("LOGIN")) return { icon: LogIn, color: "var(--ok)" };
  if (k.includes("UNLOCK")) return { icon: Unlock, color: "var(--info)" };
  if (k.includes("LOCK")) return { icon: Lock, color: "var(--info)" };
  if (k.includes("IDLE")) return { icon: Moon, color: "var(--warn)" };
  if (k.includes("USB")) return { icon: Usb, color: "var(--c7)" };
  if (k.includes("FILE")) return { icon: FileText, color: "var(--c5)" };
  if (k.includes("NETWORK")) return { icon: Wifi, color: "var(--c3)" };
  if (k.includes("PROCESS")) return { icon: Cpu, color: "var(--c4)" };
  if (k.includes("APP")) return { icon: AppWindow, color: "var(--brand)" };
  if (k.includes("AGENT")) return { icon: Server, color: "var(--muted)" };
  return { icon: Circle, color: "var(--faint)" };
}

interface Props {
  items: TimelineItem[];
  /** Show the device id on each entry (useful for employee timelines). */
  showDevice?: boolean;
}

/** Vertical timeline, grouped by local day, newest last within a day (chronological), with kind filter chips. */
export function TimelineList({ items, showDevice = true }: Props) {
  const [hidden, setHidden] = useState<Set<string>>(new Set());
  const kinds = useMemo(() => Array.from(new Set(items.map((i) => i.kind))).sort(), [items]);
  const visible = useMemo(
    () => items.filter((i) => !hidden.has(i.kind)).sort((a, b) => Date.parse(a.timestampUtc) - Date.parse(b.timestampUtc)),
    [items, hidden],
  );
  const days = useMemo(() => {
    const groups = new Map<string, TimelineItem[]>();
    for (const item of visible) {
      const day = formatDate(item.timestampUtc);
      const list = groups.get(day);
      if (list) list.push(item);
      else groups.set(day, [item]);
    }
    return Array.from(groups.entries());
  }, [visible]);

  if (items.length === 0) return <EmptyState title="No events in this period" hint="Try a different date range." />;

  return (
    <div>
      <div className="no-print mb-3 flex flex-wrap gap-1.5" role="group" aria-label="Filter by kind">
        {kinds.map((k) => {
          const on = !hidden.has(k);
          return (
            <button
              key={k}
              type="button"
              aria-pressed={on}
              onClick={() =>
                setHidden((prev) => {
                  const next = new Set(prev);
                  if (next.has(k)) next.delete(k);
                  else next.add(k);
                  return next;
                })
              }
              className={clsx("pill cursor-pointer", on ? "pill-brand" : "opacity-60 line-through")}
            >
              {humanize(k)}
            </button>
          );
        })}
      </div>

      {days.map(([day, list]) => (
        <div key={day} className="mb-5">
          <h3 className="label sticky top-0 z-[1] bg-bg py-1">{day}</h3>
          <ol className="relative ml-3 border-l border-line-strong pl-6">
            {list.map((item, i) => {
              const style = kindStyle(item);
              const Icon = style.icon;
              return (
                <li key={`${item.timestampUtc}-${item.eventType}-${i}`} className="relative pb-4 last:pb-0">
                  <span
                    className="absolute -left-[37px] flex size-6 items-center justify-center rounded-full border border-line-strong bg-surface"
                    style={{ color: style.color }}
                    aria-hidden
                  >
                    <Icon size={13} />
                  </span>
                  <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
                    <time dateTime={item.timestampUtc} title={formatUtc(item.timestampUtc)} className="mono text-muted">
                      {formatTime(item.timestampUtc)}
                    </time>
                    <span className="font-medium">{item.title}</span>
                    {item.durationSeconds != null && item.durationSeconds > 0 && (
                      <span className="pill">
                        <Duration seconds={item.durationSeconds} />
                      </span>
                    )}
                    <span className="pill" title="Event type">
                      {humanize(item.eventType)}
                    </span>
                    {showDevice && <span className="text-[12px] text-faint">{item.deviceId}</span>}
                  </div>
                  {item.detail && <p className="mt-0.5 break-words text-[12.5px] text-muted">{item.detail}</p>}
                </li>
              );
            })}
          </ol>
        </div>
      ))}
    </div>
  );
}
