/**
 * Runtime configuration. The API base URL is NOT baked into the bundle: it is resolved in the browser,
 *   1. window.__NISS_CONFIG__.apiBaseUrl (populated from /runtime-config.json, fetched once at app start)
 *   2. otherwise https://<location.hostname>:8443
 */

export interface NissRuntimeConfig {
  apiBaseUrl: string;
}

declare global {
  interface Window {
    __NISS_CONFIG__?: Partial<NissRuntimeConfig>;
  }
}

export const DEFAULT_API_PORT = 8443;

let loading: Promise<Partial<NissRuntimeConfig>> | null = null;

/** Fetch /runtime-config.json exactly once. Never throws: a missing/invalid file means "auto". */
export function loadRuntimeConfig(): Promise<Partial<NissRuntimeConfig>> {
  if (typeof window === "undefined") return Promise.resolve({});
  if (window.__NISS_CONFIG__) return Promise.resolve(window.__NISS_CONFIG__);
  if (!loading) {
    loading = fetch("/runtime-config.json", { cache: "no-store" })
      .then(async (res) => {
        if (!res.ok) return {};
        const json: unknown = await res.json();
        return sanitizeConfig(json);
      })
      .catch(() => ({}))
      .then((cfg) => {
        window.__NISS_CONFIG__ = cfg;
        return cfg;
      });
  }
  return loading;
}

export function sanitizeConfig(json: unknown): Partial<NissRuntimeConfig> {
  if (typeof json !== "object" || json === null) return {};
  const value = (json as Record<string, unknown>).apiBaseUrl;
  return typeof value === "string" ? { apiBaseUrl: value } : {};
}

/** Pure resolution rule (unit-tested). */
export function resolveApiBaseUrl(configured: string | undefined, hostname: string): string {
  const trimmed = configured?.trim();
  if (trimmed) return trimmed.replace(/\/+$/, "");
  return `https://${hostname}:${DEFAULT_API_PORT}`;
}

/** Resolve the API base URL (loads the runtime config first if needed). */
export async function getApiBaseUrl(): Promise<string> {
  const cfg = await loadRuntimeConfig();
  return resolveApiBaseUrl(cfg.apiBaseUrl, window.location.hostname);
}

/** Test helper. */
export function resetRuntimeConfigForTests(): void {
  loading = null;
  if (typeof window !== "undefined") delete window.__NISS_CONFIG__;
}
