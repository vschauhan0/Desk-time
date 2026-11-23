"use client";

import { Pause, Play, Trash2 } from "lucide-react";
import { useState } from "react";
import { Button, Card } from "@/components/ui/primitives";
import { EmptyState } from "@/components/ui/states";
import { describeEvent } from "@/lib/events";
import { formatTime, formatUtc, humanize } from "@/lib/format";
import type { EventDto } from "@/lib/types";
import { kindStyle } from "@/features/shared/timeline-list";
import { useLiveStore } from "@/store/live-store";

function FeedRow({ event }: { event: EventDto }) {
  const desc = describeEvent(event);
  const style = kindStyle({ kind: event.eventType, eventType: event.eventType });
  const Icon = style.icon;
  return (
    <li className="flash-in flex items-start gap-2.5 px-4 py-2">
      <span className="mt-0.5 shrink-0" style={{ color: style.color }} aria-hidden>
        <Icon size={15} />
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-[13px] font-medium" title={desc.title}>{desc.title}</p>
        <p className="truncate text-[11.5px] text-muted">
          {event.deviceId}
          {event.employeeCode ? ` - ${event.employeeCode}` : ""}
          {desc.detail ? ` - ${desc.detail}` : ""}
        </p>
      </div>
      <time dateTime={event.timestampUtc} title={`${humanize(event.eventType)} - ${formatUtc(event.timestampUtc)}`} className="mono shrink-0 text-faint">
        {formatTime(event.timestampUtc)}
      </time>
    </li>
  );
}

/** Live event feed fed by SignalR EventIngested (the page subscribes to the "feed" group). */
export function EventFeed({ deviceId, height = "max-h-[70vh]" }: { deviceId?: string; height?: string }) {
  const feed = useLiveStore((s) => s.feed);
  const clear = useLiveStore((s) => s.clearFeed);
  const [paused, setPaused] = useState(false);
  const [frozen, setFrozen] = useState<EventDto[]>([]);

  const source = paused ? frozen : feed;
  const items = deviceId ? source.filter((e) => e.deviceId === deviceId) : source;

  return (
    <Card
      title={`Live event feed${items.length ? ` (${items.length})` : ""}`}
      padded={false}
      actions={
        <>
          <Button
            size="sm"
            icon={paused ? Play : Pause}
            onClick={() => {
              setFrozen(feed);
              setPaused((p) => !p);
            }}
          >
            {paused ? "Resume" : "Pause"}
          </Button>
          <Button size="sm" iconOnly icon={Trash2} aria-label="Clear feed" onClick={clear} />
        </>
      }
    >
      {items.length === 0 ? (
        <EmptyState title="Waiting for events" hint="New application, session and device events appear here the moment agents upload them." />
      ) : (
        <ul className={`${height} divide-y divide-line overflow-auto`} aria-live="off">
          {items.slice(0, 150).map((e) => (
            <FeedRow key={e.eventId} event={e} />
          ))}
        </ul>
      )}
    </Card>
  );
}
