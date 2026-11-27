import { describe, expect, it } from "vitest";
import { clipFrameCount, DEFAULT_POLICY, MAX_CLIP_FRAMES, normalizePolicy, validatePolicy } from "../policy";
import type { AgentPolicy } from "../types";

/** A policy with screen capture configured on top of the shipped defaults. */
const withScreen = (over: Partial<AgentPolicy["screenCapture"]>, rest: Partial<AgentPolicy> = {}): AgentPolicy => ({
  ...DEFAULT_POLICY,
  ...rest,
  screenCapture: { ...DEFAULT_POLICY.screenCapture, ...over },
});

const fieldsOf = (p: AgentPolicy) => validatePolicy(p).map((i) => i.field);

describe("screen capture defaults", () => {
  it("ships with automatic screenshots and flagged capture switched off", () => {
    expect(DEFAULT_POLICY.screenCapture.enabled).toBe(false);
    expect(DEFAULT_POLICY.screenCapture.flaggedActivity.enabled).toBe(false);
    expect(DEFAULT_POLICY.screenCapture.flaggedActivity.terms).toEqual([]);
  });

  it("defaults the interval to a whole number of minutes", () => {
    expect(DEFAULT_POLICY.screenCapture.intervalSeconds % 60).toBe(0);
  });

  it("accepts the defaults without complaint", () => {
    expect(validatePolicy(DEFAULT_POLICY)).toEqual([]);
  });
});

describe("normalizePolicy", () => {
  it("fills in flaggedActivity when an older policy row sent null", () => {
    // A policy saved before this feature existed deserialises flaggedActivity as null. Reading .clipSeconds off
    // that null is what crashed the settings page.
    const legacy = {
      ...DEFAULT_POLICY,
      screenCapture: { ...DEFAULT_POLICY.screenCapture, enabled: true, flaggedActivity: null },
    } as unknown as AgentPolicy;

    const normalized = normalizePolicy(legacy);

    expect(normalized.screenCapture.flaggedActivity).toEqual(DEFAULT_POLICY.screenCapture.flaggedActivity);
    expect(normalized.screenCapture.flaggedActivity.enabled).toBe(false);
    expect(normalized.screenCapture.enabled).toBe(true); // the rest of the policy is preserved
  });

  it("fills in the whole screenCapture block when it is missing", () => {
    const legacy = { ...DEFAULT_POLICY, screenCapture: undefined } as unknown as AgentPolicy;

    const normalized = normalizePolicy(legacy);

    expect(normalized.screenCapture).toEqual(DEFAULT_POLICY.screenCapture);
  });

  it("leaves a complete policy untouched", () => {
    expect(normalizePolicy(DEFAULT_POLICY)).toEqual(DEFAULT_POLICY);
  });

  it("produces a policy the editor can render without throwing", () => {
    const legacy = { ...DEFAULT_POLICY, screenCapture: undefined } as unknown as AgentPolicy;
    const n = normalizePolicy(legacy);

    expect(() => clipFrameCount(n.screenCapture.flaggedActivity.clipSeconds, n.screenCapture.flaggedActivity.clipFps)).not.toThrow();
  });
});

describe("clipFrameCount", () => {
  it("multiplies length by frame rate", () => {
    expect(clipFrameCount(10, 2)).toBe(20);
    expect(clipFrameCount(5, 1)).toBe(5);
  });

  it("clamps absurd values instead of producing thousands of frames", () => {
    expect(clipFrameCount(600, 60)).toBe(MAX_CLIP_FRAMES);
    expect(clipFrameCount(0, 0)).toBe(1);
    expect(clipFrameCount(-5, -5)).toBe(1);
  });
});

describe("screen capture validation", () => {
  it("rejects an interval below the agent's own 5 second floor", () => {
    expect(fieldsOf(withScreen({ intervalSeconds: 4 }))).toContain("screenCapture.intervalSeconds");
    expect(fieldsOf(withScreen({ intervalSeconds: 5 }))).not.toContain("screenCapture.intervalSeconds");
  });

  it("rejects out-of-range quality and width", () => {
    expect(fieldsOf(withScreen({ quality: 0 }))).toContain("screenCapture.quality");
    expect(fieldsOf(withScreen({ quality: 101 }))).toContain("screenCapture.quality");
    expect(fieldsOf(withScreen({ maxWidth: 100 }))).toContain("screenCapture.maxWidth");
  });

  it("flags an enabled flag list with no terms, since nothing would ever be captured", () => {
    const issues = fieldsOf(withScreen({ flaggedActivity: { ...DEFAULT_POLICY.screenCapture.flaggedActivity, enabled: true, terms: [] } }));
    expect(issues).toContain("screenCapture.flaggedActivity.terms");
  });

  it("accepts an enabled flag list that has terms", () => {
    const issues = fieldsOf(
      withScreen({ flaggedActivity: { ...DEFAULT_POLICY.screenCapture.flaggedActivity, enabled: true, terms: ["facebook"] } }),
    );
    expect(issues).toEqual([]);
  });

  it("warns when flagged capture is on but window titles are not captured", () => {
    // Sites are recognised from the window title, so the feature silently under-delivers without it.
    const issues = fieldsOf(
      withScreen(
        { flaggedActivity: { ...DEFAULT_POLICY.screenCapture.flaggedActivity, enabled: true, terms: ["facebook"] } },
        { captureWindowTitles: false },
      ),
    );
    expect(issues).toContain("captureWindowTitles");
  });

  it("does not warn about window titles when flagged capture is off", () => {
    expect(fieldsOf(withScreen({}, { captureWindowTitles: false }))).not.toContain("captureWindowTitles");
  });

  it("bounds clip length and frame rate", () => {
    const fa = DEFAULT_POLICY.screenCapture.flaggedActivity;
    expect(fieldsOf(withScreen({ flaggedActivity: { ...fa, clipSeconds: 61 } }))).toContain("screenCapture.flaggedActivity.clipSeconds");
    expect(fieldsOf(withScreen({ flaggedActivity: { ...fa, clipFps: 6 } }))).toContain("screenCapture.flaggedActivity.clipFps");
    expect(fieldsOf(withScreen({ flaggedActivity: { ...fa, cooldownSeconds: -1 } }))).toContain("screenCapture.flaggedActivity.cooldownSeconds");
  });
});
