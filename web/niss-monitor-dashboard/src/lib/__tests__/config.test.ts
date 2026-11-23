import { afterEach, describe, expect, it, vi } from "vitest";
import { getApiBaseUrl, loadRuntimeConfig, resetRuntimeConfigForTests, resolveApiBaseUrl, sanitizeConfig } from "../config";

afterEach(() => {
  resetRuntimeConfigForTests();
  vi.unstubAllGlobals();
});

describe("resolveApiBaseUrl", () => {
  it("uses the configured URL without trailing slash", () => {
    expect(resolveApiBaseUrl("https://central:8443/", "ignored")).toBe("https://central:8443");
  });
  it("falls back to https://<host>:8443 when empty or missing", () => {
    expect(resolveApiBaseUrl("", "pc-central")).toBe("https://pc-central:8443");
    expect(resolveApiBaseUrl("  ", "pc-central")).toBe("https://pc-central:8443");
    expect(resolveApiBaseUrl(undefined, "192.168.1.5")).toBe("https://192.168.1.5:8443");
  });
});

describe("sanitizeConfig", () => {
  it("accepts only a string apiBaseUrl", () => {
    expect(sanitizeConfig({ apiBaseUrl: "x" })).toEqual({ apiBaseUrl: "x" });
    expect(sanitizeConfig({ apiBaseUrl: 5 })).toEqual({});
    expect(sanitizeConfig(null)).toEqual({});
    expect(sanitizeConfig("str")).toEqual({});
  });
});

describe("loadRuntimeConfig", () => {
  it("fetches /runtime-config.json once and stores it on window.__NISS_CONFIG__", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ apiBaseUrl: "https://api.lan:9000" }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    await loadRuntimeConfig();
    await loadRuntimeConfig();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(window.__NISS_CONFIG__?.apiBaseUrl).toBe("https://api.lan:9000");
    expect(await getApiBaseUrl()).toBe("https://api.lan:9000");
  });
  it("treats a missing file as automatic mode", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("nope", { status: 404 })));
    expect(await getApiBaseUrl()).toBe(`https://${window.location.hostname}:8443`);
  });
  it("treats a network failure as automatic mode", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => {
      throw new TypeError("offline");
    }));
    expect(await getApiBaseUrl()).toBe(`https://${window.location.hostname}:8443`);
  });
});
