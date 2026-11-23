# NISS PC Monitor - web dashboard

Administrator dashboard for the NISS Employee PC Monitoring System (transparent, organisation-managed monitoring).
Next.js (App Router) + React + TypeScript (strict) + Tailwind CSS 4 + Recharts + `@microsoft/signalr` + zustand + SWR.
No cloud services, no external fonts or assets: it runs entirely on the LAN.

The dashboard only talks to the NISS API (`server/`). All DTOs are mirrored from the C# contract
(`src/NISS.Monitor.Protocol`) in `src/lib/types.ts`.

## Commands

Requires Node 20+ (developed on Node 24, npm 11).

```powershell
cd web\niss-monitor-dashboard
npm install            # install
npm run dev            # development server, http://0.0.0.0:3000
npm run build          # production build
npm start              # production server, http://0.0.0.0:3000  (run `npm run build` first)
npm test               # Vitest unit tests
npm run lint           # ESLint (must be clean)
npm run typecheck      # tsc --noEmit (strict)
```

Both `dev` and `start` bind `0.0.0.0` so other PCs on the LAN can open `http://<central-pc>:3000`.

### Port

Next.js reads the port from the **process environment** (`PORT`), not from `.env` files. Default is 3000.

```powershell
$env:PORT = "8080"; npm start          # PowerShell
set PORT=8080 && npm start             # cmd.exe
npx next start -H 0.0.0.0 -p 8080      # or pass it explicitly
```

## Configuring the API address (runtime, not build time)

The API base URL is resolved **in the browser at start-up**, so the same build works on any network:

1. `public/runtime-config.json` is fetched once (before any API call) and stored as `window.__NISS_CONFIG__`.
   `{ "apiBaseUrl": "https://central-pc:8443" }`
2. If `apiBaseUrl` is empty or the file is missing: `https://<hostname you opened the dashboard on>:8443`.

The shipped file is `{"apiBaseUrl": ""}` (automatic). To change it on a deployed instance, edit
`public/runtime-config.json` (or `.next/standalone/public/...` if you deploy standalone) and reload the page - no rebuild.

The API uses a **self-signed LAN certificate** and its CORS policy must allow the dashboard origin
(`http://<host>:3000`) and the `Authorization`, `Content-Type` and `X-Requested-With` request headers
(SignalR sends `X-Requested-With` on negotiation). The dashboard uses bearer tokens, not cookies
(`withCredentials: false`), so `Access-Control-Allow-Credentials` is not required.
Browsers reject a self-signed certificate for `fetch`/WebSocket until it has been accepted once:
open `https://<central-pc>:8443/api/v1/health` in the same browser and accept the warning.
If the API cannot be reached the dashboard shows a full-page explanation with that exact instruction.

## Sign-in and token handling

* `POST /auth/login` returns a 15-minute JWT access token and a rotating 8-hour refresh token.
* **Access token: memory only** (never persisted). **Refresh token: `sessionStorage`** so a reload keeps the session
  but closing the tab ends it.
* Tradeoff: `sessionStorage` is readable by any script on this origin (XSS). Mitigations: the refresh token rotates on
  every use, the app ships no third-party scripts (no CDNs, no analytics, system fonts), and the access token that
  authorises API calls never touches storage. An HttpOnly cookie would resist XSS better but needs same-site hosting
  and CSRF protection, which the `:3000` dashboard / `:8443` API topology does not offer.
* On a `401` the client refreshes once (single-flight, shared between concurrent requests), retries the request and,
  if the refresh fails, clears the session and redirects to `/login`.
* Roles: `VIEWER` read-only; `ADMIN` sees actions (device disable/enable/revoke/re-enroll, resolve security events,
  enrollment tokens, employees, settings, audit log, CSV export); `SUPERADMIN` also manages administrator accounts.
  The UI hides what a role cannot do; the API remains the authority (403s are shown as such).

## Real-time updates

One shared SignalR connection (`/hubs/monitor`, `accessTokenFactory` always returns a fresh token, automatic reconnect
with capped back-off, endless initial-connect retry) lives in `src/providers/signalr-provider.tsx` and feeds a zustand
store (`src/store/live-store.ts`):

| Server push | Effect |
|---|---|
| `DeviceStateChanged` | replaces the device's live card |
| `DeviceStatusChanged` | patches the card status, refreshes overview/security queries |
| `EventIngested` | prepended to the live feed (needs `SubscribeFeed` / `SubscribeDevice`, ref-counted, re-sent after reconnect) |
| `SecurityEventRaised` | stored, refreshes overview/security queries |

After every (re)connect the Live page refetches `GET /live` so nothing is lost while the socket was down.
The header shows the connection state (Live / Connecting / Reconnecting / Offline).

## Pages and the API endpoints they use

| Page | Route | Endpoints |
|---|---|---|
| Overview | `/` | `/overview` (30 s poll + live invalidation) |
| Live Monitoring | `/live` | `/live`, `/departments`, SignalR |
| Employees | `/employees` | `/employees`, `/departments`; `POST/PUT /employees` (ADMIN) |
| Employee detail | `/employees/[id]` | `/employees/{id}/summary`, `/employees/{id}/timeline` |
| Devices | `/devices` | `/devices`, `/employees`, `/departments`; ADMIN: `POST /devices/{id}/disable\|enable\|revoke\|re-enroll`, `POST /enrollment-tokens` |
| Device detail | `/devices/[deviceId]?tab=` | `/devices/{id}`, `/activity`, `/applications`, `/processes`, `/metrics`, `/timeline`, `/diagnostics`, `/diagnostics/download`; SignalR `SubscribeDevice` |
| Application Activity | `/applications` | `/applications` |
| Process Timeline | `/processes` | `/processes` |
| Employee Timeline | `/timeline` | `/employees`, `/employees/{id}/timeline` |
| Idle / Active | `/active-idle` | `/reports/active-idle` |
| Device Health | `/device-health` | `/live`, `/reports/device-health`, `/devices/{id}/metrics` (sparkline) |
| USB Events | `/usb` | `/usb-events` |
| File Activity | `/files` | `/file-events`, `/settings/policies` (ADMIN, to explain the disabled-by-default state) |
| Security & Agent Health | `/security` | `/security-events`, `POST /security-events/{id}/resolve` (ADMIN) |
| Reports | `/reports` | `/reports/{kind}` (`format=json` and `format=csv`) |
| Settings (ADMIN+) | `/settings` | `/settings/policies[/{id}]`, `/settings/app-categories`, `/settings/retention`, `/settings/health-thresholds`, `/enrollment-tokens`, `/admin-users` (SUPERADMIN) |
| Audit Log (ADMIN+) | `/audit` | `/audit-logs` |

Global filters (`FilterBar` + `useDateRange`): Employee, Department, Device, Application, Process, Event type, Status,
Category, free-text search and the date presets Today / Yesterday / Last 7 days / Custom. Only the filters a page's
endpoint supports are shown. Times are displayed in the viewer's time zone with the UTC time in the tooltip; durations
read like `2h 18m`.

## Design rules that are deliberate

* **Running / Foreground / Interacting are never merged.** Application tables show *Open / running*, *In foreground* and
  *Actively used* as separate columns/series; live cards show three separate badges per application.
* **No productivity scoring.** Nothing is labelled "productive"/"unproductive"; reports are objective telemetry only.
* Privacy switches (window titles, command lines, file monitoring, cursor sampling) are visible in the policy editor
  with warnings; file monitoring is described as disabled by default.

## Project layout

```
src/app/                 routes (thin pages) - (app)/ group is behind the login guard
src/features/<area>/     page-level components per feature (overview, live, employees, devices, ...)
src/components/          ui primitives, charts, filter bar, layout shell
src/providers/           auth + SignalR providers
src/store/               zustand live store
src/hooks/               useApi (SWR), useDateRange, useFilters, useNow, ...
src/lib/                 types.ts (C# mirror), api.ts (typed client), format.ts, filters.ts, config.ts, tokens.ts
public/runtime-config.json
```

## Tests

`npm test` runs Vitest (jsdom): formatters, date-range/filter logic, the API client (auth header, single-flight
refresh-on-401, problem-details parsing, unreachable server, downloads), runtime config resolution, the live store
reducers, Gantt layout, report helpers and policy validation.

## Print

The Reports page has a Print button; a print stylesheet hides navigation and filters and prints the table on white.
