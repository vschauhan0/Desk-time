"use client";

import { Field, Notice } from "@/components/ui/primitives";
import { clipFrameCount } from "@/lib/policy";
import type { FlaggedActivityPolicy, ScreenCapturePolicy } from "@/lib/types";
import { ListField, NumberField, SectionTitle, ToggleField } from "./form-fields";

/** Screenshot intervals are configured in whole minutes; the wire contract stays in seconds. */
const SECONDS_PER_MINUTE = 60;

/**
 * Screen capture + flagged activity fields. Renders bare grid items so it drops into the policy form's existing
 * three-column grid, same as the other sections.
 */
export function ScreenCaptureFields({
  value,
  captureWindowTitles,
  onChange,
}: {
  value: ScreenCapturePolicy;
  captureWindowTitles: boolean;
  onChange: (v: ScreenCapturePolicy) => void;
}) {
  const set = <K extends keyof ScreenCapturePolicy>(key: K, v: ScreenCapturePolicy[K]) => onChange({ ...value, [key]: v });
  const setFlag = <K extends keyof FlaggedActivityPolicy>(key: K, v: FlaggedActivityPolicy[K]) =>
    onChange({ ...value, flaggedActivity: { ...value.flaggedActivity, [key]: v } });

  const flagged = value.flaggedActivity;
  const minutes = Math.max(1, Math.round(value.intervalSeconds / SECONDS_PER_MINUTE));
  // An interval set through the API need not be a whole number of minutes; say so rather than silently rounding.
  const offGrid = value.intervalSeconds !== minutes * SECONDS_PER_MINUTE;
  const frames = clipFrameCount(flagged.clipSeconds, flagged.clipFps);

  return (
    <>
      <ToggleField
        label="Automatic screenshots"
        checked={value.enabled}
        onChange={(v) => set("enabled", v)}
        hint="Takes a still of every monitor on the interval below. Never captures while the screen is locked or the user is idle."
        warn
      />
      <NumberField
        label="Capture every"
        unit="minutes"
        min={1}
        max={240}
        value={minutes}
        onChange={(n) => set("intervalSeconds", Math.max(1, n) * SECONDS_PER_MINUTE)}
        hint={offGrid ? `Currently ${value.intervalSeconds}s; saving rounds it to ${minutes} min.` : `= ${value.intervalSeconds} seconds between captures.`}
      />
      <NumberField
        label="Image quality"
        unit="1-100"
        min={1}
        max={100}
        value={value.quality}
        onChange={(n) => set("quality", n)}
        hint="JPEG quality. Lower means smaller files and less disk use."
      />
      <NumberField
        label="Max width"
        unit="px"
        min={320}
        max={7680}
        value={value.maxWidth}
        onChange={(n) => set("maxWidth", n)}
        hint="Wider screens are downscaled to this width before encoding."
      />
      <ToggleField
        label="Live screen view"
        checked={value.liveEnabled}
        onChange={(v) => set("liveEnabled", v)}
        hint="Lets a superadmin watch this screen on demand. Live frames are never stored."
      />
      <div className="flex items-end text-[12px] text-faint">
        Stored screenshots are deleted automatically — see the Retention tab.
      </div>

      <SectionTitle>Flagged activity</SectionTitle>
      <div className="col-span-full">
        <Notice tone="info">
          A flagged term is matched against the <strong>window title</strong> and the process name, case-insensitively.
          No web address is ever read or stored — a browser window title carries the <em>site name</em>, so
          <span className="mono"> youtube.com</span> also matches its bare name <span className="mono">youtube</span>,
          which is what appears in a title like <span className="mono">… - YouTube - Chrome</span>. Type either form.
          A site whose page title contains neither will not be caught.
        </Notice>
      </div>
      <ToggleField
        label="Capture on flagged activity"
        checked={flagged.enabled}
        onChange={(v) => setFlag("enabled", v)}
        hint="Takes a screenshot as soon as a flagged site or app comes to the foreground. Works even with automatic screenshots off."
        warn
      />
      <ToggleField
        label="Also record a clip"
        checked={flagged.captureClip}
        onChange={(v) => setFlag("captureClip", v)}
        hint="Records a short burst of frames after the flag, replayable in the device's Screens tab."
      />
      <NumberField
        label="Cooldown per term"
        unit="s"
        min={0}
        max={86400}
        value={flagged.cooldownSeconds}
        onChange={(n) => setFlag("cooldownSeconds", n)}
        hint="Minimum gap before the same term can fire again, so one flagged site cannot flood storage."
      />
      <div className="col-span-full">
        <ListField
          label="Flagged sites & apps"
          value={flagged.terms}
          onChange={(v) => setFlag("terms", v)}
          placeholder={"facebook\nyoutube\ninstagram\nbetting\nsteam.exe"}
          hint={
            <>
              One term per line. Scope this policy to a single device (the Policies list, “New”) to flag different sites
              per PC.
              {flagged.enabled && flagged.terms.length === 0 && (
                <strong className="text-warn"> Flagged capture is on but no terms are listed, so nothing will be captured.</strong>
              )}
              {flagged.enabled && !captureWindowTitles && (
                <strong className="text-warn"> “Capture window titles” is off, so only process-name terms can match.</strong>
              )}
            </>
          }
        />
      </div>
      {flagged.captureClip && (
        <>
          <NumberField
            label="Clip length"
            unit="s"
            min={1}
            max={60}
            value={flagged.clipSeconds}
            onChange={(n) => setFlag("clipSeconds", n)}
          />
          <NumberField
            label="Clip frame rate"
            unit="fps"
            min={1}
            max={5}
            value={flagged.clipFps}
            onChange={(n) => setFlag("clipFps", n)}
          />
          <Field label="Frames per clip">
            <input className="field" value={`${frames} frame${frames === 1 ? "" : "s"}`} readOnly disabled />
            <span className="mt-1 block text-[11.5px] text-faint">
              A clip is a sequence of stills the dashboard replays, not a video file.
            </span>
          </Field>
        </>
      )}
    </>
  );
}
