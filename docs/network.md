# Network configuration guide

Everything is LAN-only. Example: central PC `192.168.1.222`.

| Component | Where | Default | Configure in |
|---|---|---|---|
| API (REST + SignalR) | central PC | `https://192.168.1.222:8443` | `server\NISS.Api\serversettings.json` -> `Server:BindIp`, `Server:Port` |
| Dashboard | central PC | `http://192.168.1.222:3000` | `serversettings.json` -> `Dashboard:Port` (used by CORS + the launcher) |
| PostgreSQL | central PC | `localhost:5432` | `serversettings.json` -> `Database:ConnectionString` |
| Agent -> API | each employee PC | `Agent:Server:BaseUrl` | `C:\Program Files\NISS\Monitor\agentsettings.json` |

## Ports and firewall (central PC)

```powershell
# elevated, allow only your LAN subnet
New-NetFirewallRule -DisplayName 'NISS API'       -Direction Inbound -Protocol TCP -LocalPort 8443 -RemoteAddress 192.168.1.0/24 -Action Allow
New-NetFirewallRule -DisplayName 'NISS Dashboard' -Direction Inbound -Protocol TCP -LocalPort 3000 -RemoteAddress 192.168.1.0/24 -Action Allow
```

PostgreSQL does **not** need to be reachable from the LAN - keep it bound to localhost.
Employee PCs only need outbound TCP to `<central-pc>:8443`.

## Give the central PC a stable address

Use a DHCP reservation (recommended) or a static IP, or a DNS name (`niss.local`) that resolves on the LAN. If you use a DNS name,
create the certificate with it: `.\deployment\certificates\New-NissServerCertificate.ps1 -ExtraName niss.local` and use
`https://niss.local:8443` in the installer. The certificate already contains `localhost`, the computer name and every LAN IPv4 of the PC.

## Certificates

* `New-NissServerCertificate.ps1` creates `server.pfx` (private, git-ignored) and `server.cer` (public) and prints the **SHA-256 pin**.
* Agents are installed with `--thumbprint <pin>`; they then accept exactly that certificate (no CA needed, no validation bypass).
* Admin browsers: run `Trust-NissCertificate.ps1` (elevated) on each admin PC, or import `server.cer` into *Trusted Root Certification Authorities*.
  The dashboard talks to `https://<central-pc>:8443` from the browser, so the browser must trust it (or you must visit the API URL once and accept it).
* If the central PC's IP changes, re-run the script with `-Force` and update agents (`NISSAgent.exe --enroll` is not needed; change `BaseUrl`/pin in `agentsettings.json` and restart the service, or repair with the installer).

## Dashboard -> API address

The dashboard is served by Next.js on port 3000 and calls the API directly from the browser. It resolves the API base URL at runtime:
`public/runtime-config.json` `apiBaseUrl` (written by `NISS.Server.Start.ps1`), otherwise `https://<the host you opened the dashboard on>:8443`.
So opening `http://192.168.1.222:3000` automatically talks to `https://192.168.1.222:8443`.

## Clock synchronisation

Agents measure their offset to the server at every heartbeat and correct timestamps, but keep the central PC and endpoints on NTP (domain time or `time.windows.com`) anyway.
