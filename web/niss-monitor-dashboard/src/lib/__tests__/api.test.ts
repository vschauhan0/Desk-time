import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ApiError,
  NetworkError,
  api,
  buildPath,
  buildQueryString,
  filenameFromDisposition,
  getValidAccessToken,
  request,
  setApiHooks,
} from "../api";
import { resetRuntimeConfigForTests } from "../config";
import { tokenStore } from "../tokens";
import type { AuthResponse } from "../types";

const BASE = "https://niss.test:8443";

function auth(access: string, refresh: string, expiresInMs = 15 * 60_000): AuthResponse {
  return {
    accessToken: access,
    refreshToken: refresh,
    expiresAtUtc: new Date(Date.now() + expiresInMs).toISOString(),
    user: { id: "u1", username: "admin", displayName: "Admin", role: "ADMIN", pageAccess: null },
  };
}

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;

function mockFetch(handler: Handler) {
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => handler(String(input), init ?? {}));
  vi.stubGlobal("fetch", fn);
  return fn;
}

function bearer(init: RequestInit): string | undefined {
  return (init.headers as Record<string, string>).Authorization;
}

beforeEach(() => {
  window.__NISS_CONFIG__ = { apiBaseUrl: BASE };
  tokenStore.clear();
  setApiHooks({});
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetRuntimeConfigForTests();
  tokenStore.clear();
  setApiHooks({});
});

describe("query building", () => {
  it("skips empty values and repeats array keys", () => {
    expect(buildQueryString({ a: 1, b: "", c: null, d: undefined, e: ["x", "y"], f: false })).toBe("?a=1&e=x&e=y&f=false");
    expect(buildQueryString({})).toBe("");
    expect(buildQueryString(undefined)).toBe("");
  });
  it("encodes special characters", () => {
    expect(buildQueryString({ search: "a b&c" })).toBe("?search=a+b%26c");
  });
  it("appends to a path that already has a query string", () => {
    expect(buildPath("/devices", { page: 2 })).toBe("/devices?page=2");
    expect(buildPath("/devices?status=ONLINE", { page: 2 })).toBe("/devices?status=ONLINE&page=2");
    expect(buildPath("/devices", {})).toBe("/devices");
  });
});

describe("request", () => {
  it("calls the runtime-configured base URL with the bearer token", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    const f = mockFetch(() => json({ totalDevices: 3 }));
    const res = await api.overview();
    expect(res).toEqual({ totalDevices: 3 });
    const [url, init] = f.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`${BASE}/api/v1/overview`);
    expect(bearer(init)).toBe("Bearer tok-1");
  });

  it("serialises JSON bodies and query strings", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    const f = mockFetch(() => json({ ok: true }));
    await request("/x", { method: "POST", query: { a: 1 }, body: { hello: "world" } });
    const [url, init] = f.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`${BASE}/api/v1/x?a=1`);
    expect(init.method).toBe("POST");
    expect(init.body).toBe('{"hello":"world"}');
    expect((init.headers as Record<string, string>)["Content-Type"]).toBe("application/json");
  });

  it("returns undefined for 204 responses", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    mockFetch(() => new Response(null, { status: 204 }));
    await expect(request<void>("/devices/PC-1/disable", { method: "POST" })).resolves.toBeUndefined();
  });

  it("does not send Authorization to anonymous endpoints", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    const f = mockFetch(() => json({ status: "Healthy", version: "1", serverTimeUtc: "", database: true }));
    await api.health();
    const [, init] = f.mock.calls[0] as [string, RequestInit];
    expect(bearer(init)).toBeUndefined();
  });
});

describe("problem details", () => {
  it("parses application/problem+json into an ApiError", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    mockFetch(() =>
      new Response(JSON.stringify({ title: "Bad Request", status: 400, detail: "Invalid policy", errors: { HeartbeatSeconds: ["must be > 0"] } }), {
        status: 400,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    const err = await request("/settings/policies/1", { method: "PUT", body: {} }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    const e = err as ApiError;
    expect(e.status).toBe(400);
    expect(e.title).toBe("Bad Request");
    expect(e.detail).toBe("Invalid policy");
    expect(e.errors).toEqual({ HeartbeatSeconds: ["must be > 0"] });
    expect(e.message).toContain("Invalid policy");
    expect(e.message).toContain("HeartbeatSeconds: must be > 0");
  });

  it("falls back to status text for non-JSON bodies", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    mockFetch(() => new Response("<html>oops</html>", { status: 502, statusText: "Bad Gateway" }));
    const e = (await request("/x").catch((err: unknown) => err)) as ApiError;
    expect(e.status).toBe(502);
    expect(e.message).toBe("HTTP 502 Bad Gateway");
  });

  it("surfaces 403 as an ApiError", async () => {
    tokenStore.set(auth("tok-1", "ref-1"));
    mockFetch(() => json({ title: "Forbidden", status: 403 }, 403, { "Content-Type": "application/problem+json" }));
    const e = (await request("/audit-logs").catch((err: unknown) => err)) as ApiError;
    expect(e.status).toBe(403);
  });
});

describe("token refresh", () => {
  it("refreshes once on 401, retries with the new token and rotates the stored refresh token", async () => {
    tokenStore.set(auth("old", "ref-1"));
    const f = mockFetch((url, init) => {
      if (url.endsWith("/auth/refresh")) return json(auth("new", "ref-2"));
      return bearer(init) === "Bearer new" ? json({ ok: 1 }) : json({ title: "Unauthorized" }, 401);
    });
    await expect(request<{ ok: number }>("/overview")).resolves.toEqual({ ok: 1 });
    const urls = f.mock.calls.map((c) => c[0]);
    expect(urls).toEqual([`${BASE}/api/v1/overview`, `${BASE}/api/v1/auth/refresh`, `${BASE}/api/v1/overview`]);
    expect(JSON.parse((f.mock.calls[1][1] as RequestInit).body as string)).toEqual({ refreshToken: "ref-1" });
    expect(tokenStore.getAccess()).toBe("new");
    expect(tokenStore.getRefresh()).toBe("ref-2");
  });

  it("shares one refresh between concurrent 401s", async () => {
    tokenStore.set(auth("old", "ref-1"));
    let refreshCalls = 0;
    mockFetch(async (url, init) => {
      if (url.endsWith("/auth/refresh")) {
        refreshCalls++;
        await new Promise((r) => setTimeout(r, 10));
        return json(auth("new", "ref-2"));
      }
      return bearer(init) === "Bearer new" ? json({ ok: 1 }) : json({}, 401);
    });
    await Promise.all([request("/a"), request("/b"), request("/c")]);
    expect(refreshCalls).toBe(1);
  });

  it("clears tokens and signals auth loss when the refresh fails", async () => {
    tokenStore.set(auth("old", "ref-1"));
    const onAuthLost = vi.fn();
    setApiHooks({ onAuthLost });
    mockFetch((url) => (url.endsWith("/auth/refresh") ? json({ title: "Invalid refresh token" }, 401) : json({}, 401)));
    const e = (await request("/overview").catch((err: unknown) => err)) as ApiError;
    expect(e).toBeInstanceOf(ApiError);
    expect(e.status).toBe(401);
    expect(onAuthLost).toHaveBeenCalledTimes(1);
    expect(tokenStore.getAccess()).toBeNull();
    expect(tokenStore.getRefresh()).toBeNull();
  });

  it("gives up if the retried request is still 401", async () => {
    tokenStore.set(auth("old", "ref-1"));
    const onAuthLost = vi.fn();
    setApiHooks({ onAuthLost });
    mockFetch((url) => (url.endsWith("/auth/refresh") ? json(auth("new", "ref-2")) : json({}, 401)));
    const e = (await request("/overview").catch((err: unknown) => err)) as ApiError;
    expect(e.status).toBe(401);
    expect(onAuthLost).toHaveBeenCalledTimes(1);
  });

  it("restores a session after a page reload (refresh token only, no access token)", async () => {
    sessionStorage.setItem("niss.refreshToken", "ref-9");
    expect(tokenStore.getAccess()).toBeNull();
    const f = mockFetch((url, init) => {
      if (url.endsWith("/auth/refresh")) return json(auth("fresh", "ref-10"));
      return bearer(init) === "Bearer fresh" ? json({ id: "u1" }) : json({}, 401);
    });
    await expect(api.me()).resolves.toEqual({ id: "u1" });
    expect(f.mock.calls[0][0]).toBe(`${BASE}/api/v1/auth/refresh`);
  });

  it("proactively refreshes an access token that is about to expire", async () => {
    tokenStore.set(auth("almost-expired", "ref-1", 5_000));
    const f = mockFetch((url) => (url.endsWith("/auth/refresh") ? json(auth("renewed", "ref-2")) : json({})));
    await request("/overview");
    expect(f.mock.calls[0][0]).toBe(`${BASE}/api/v1/auth/refresh`);
    expect(tokenStore.getAccess()).toBe("renewed");
  });

  it("getValidAccessToken (SignalR accessTokenFactory) refreshes when needed", async () => {
    tokenStore.set(auth("almost-expired", "ref-1", 1_000));
    mockFetch(() => json(auth("renewed", "ref-2")));
    await expect(getValidAccessToken()).resolves.toBe("renewed");
  });
});

describe("unreachable server", () => {
  it("throws NetworkError carrying the base URL and reports reachability", async () => {
    tokenStore.set(auth("tok", "ref"));
    const onReachability = vi.fn();
    setApiHooks({ onReachability });
    mockFetch(() => {
      throw new TypeError("Failed to fetch");
    });
    const e = (await request("/overview").catch((err: unknown) => err)) as NetworkError;
    expect(e).toBeInstanceOf(NetworkError);
    expect(e.baseUrl).toBe(BASE);
    expect(e.message).toBe(`Cannot reach NISS server at ${BASE}`);
    expect(onReachability).toHaveBeenCalledWith(false, BASE);
  });

  it("reports reachable again after any HTTP response", async () => {
    tokenStore.set(auth("tok", "ref"));
    const onReachability = vi.fn();
    setApiHooks({ onReachability });
    mockFetch(() => json({}));
    await request("/overview");
    expect(onReachability).toHaveBeenLastCalledWith(true, BASE);
  });
});

describe("login", () => {
  it("posts credentials anonymously", async () => {
    const f = mockFetch(() => json(auth("a", "r")));
    const res = await api.login("admin", "pw");
    expect(res.accessToken).toBe("a");
    const [url, init] = f.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`${BASE}/api/v1/auth/login`);
    expect(JSON.parse(init.body as string)).toEqual({ username: "admin", password: "pw" });
    expect(bearer(init)).toBeUndefined();
  });

  it("does not try to refresh on a failed login (401)", async () => {
    sessionStorage.setItem("niss.refreshToken", "ref-x");
    const f = mockFetch(() => json({ title: "Invalid credentials", status: 401 }, 401, { "Content-Type": "application/problem+json" }));
    const e = (await api.login("a", "b").catch((err: unknown) => err)) as ApiError;
    expect(e.status).toBe(401);
    expect(e.message).toBe("Invalid credentials");
    expect(f).toHaveBeenCalledTimes(1);
  });
});

describe("filenameFromDisposition", () => {
  it("reads plain, quoted and RFC 6266 filenames", () => {
    expect(filenameFromDisposition('attachment; filename="diag-PC-1.json"', "x")).toBe("diag-PC-1.json");
    expect(filenameFromDisposition("attachment; filename=report.csv", "x")).toBe("report.csv");
    expect(filenameFromDisposition("attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.csv", "x")).toBe("résumé.csv");
    expect(filenameFromDisposition(null, "fallback.json")).toBe("fallback.json");
  });
});
