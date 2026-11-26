import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, setApiHooks } from "../api";
import { resetRuntimeConfigForTests } from "../config";
import { tokenStore } from "../tokens";
import type { AuthResponse } from "../types";

const BASE = "https://niss.test:8443";

function auth(): AuthResponse {
  return {
    accessToken: "tok-1",
    refreshToken: "ref-1",
    expiresAtUtc: new Date(Date.now() + 15 * 60_000).toISOString(),
    user: { id: "u1", username: "admin", displayName: "Admin", role: "SUPERADMIN" },
  };
}

function captureFetch(response: () => Response) {
  const calls: Array<{ url: string; method: string }> = [];
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: (init?.method ?? "GET").toUpperCase() });
    return response();
  });
  vi.stubGlobal("fetch", fn);
  return calls;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

beforeEach(() => {
  window.__NISS_CONFIG__ = { apiBaseUrl: BASE };
  tokenStore.set(auth());
  setApiHooks({});
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetRuntimeConfigForTests();
  tokenStore.clear();
  setApiHooks({});
});

describe("screenshot + live-view API", () => {
  it("lists screenshots with paging query", async () => {
    const calls = captureFetch(() => json({ items: [], total: 0, page: 1, pageSize: 120 }));
    await api.screenshots("PC-001", { pageSize: 120 });
    expect(calls[0].url).toBe(`${BASE}/api/v1/devices/PC-001/screenshots?pageSize=120`);
  });

  it("fetches a screenshot image as a blob from the image endpoint", async () => {
    const calls = captureFetch(() => new Response(new Uint8Array([255, 216, 255]), { status: 200, headers: { "Content-Type": "image/jpeg" } }));
    const blob = await api.screenshotBlob("PC-001", "abc");
    expect(calls[0].url).toBe(`${BASE}/api/v1/devices/PC-001/screenshots/abc/image`);
    expect(blob.size).toBe(3);
  });

  it("starts live view with the monitor as a POST query", async () => {
    const calls = captureFetch(() => json({ liveSessionId: "s1", deviceId: "PC-001", hubGroup: "live:s1", monitor: 0, startedAtUtc: "" }));
    const session = await api.startLiveView("PC-001", 0);
    expect(calls[0]).toEqual({ url: `${BASE}/api/v1/devices/PC-001/live-view/start?monitor=0`, method: "POST" });
    expect(session.liveSessionId).toBe("s1");
  });

  it("stops and switches live view by session id", async () => {
    const calls = captureFetch(() => new Response(null, { status: 204 }));
    await api.stopLiveView("PC-001", "s1");
    await api.switchLiveView("PC-001", "s1", 2);
    expect(calls[0].url).toBe(`${BASE}/api/v1/devices/PC-001/live-view/stop?liveSessionId=s1`);
    expect(calls[1].url).toBe(`${BASE}/api/v1/devices/PC-001/live-view/switch?liveSessionId=s1&monitor=2`);
  });
});
