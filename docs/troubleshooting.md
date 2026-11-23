# Troubleshooting guide

Start with `NISSAgent.exe --status` and `NISSAgent.exe --health` (elevated) on the PC, and the device's **Diagnostics** tab in the dashboard.
Agent logs: `C:\ProgramData\NISS\Agent\Logs\agent-YYYYMMDD.log`. Windows Event Log: Application, source `NISS Monitor Service` (warnings/errors).
Session-process log: `%LOCALAPPDATA%\NISS\Session\session.log`. Server logs: `logs\` (Serilog) and `logs\api-console.log`.

| Symptom | Likely cause / fix |
|---|---|
| Device never appears in the dashboard | `--health` shows why. Not enrolled (`exit 4`): run `--enroll` with a fresh token. Unreachable (`exit 2`): firewall, wrong `BaseUrl`, server down. |
| `--health` says UNREACHABLE but the URL opens in a browser | Certificate pin mismatch. Re-create/print the pin (`New-NissServerCertificate.ps1`) and set `Agent:Server:CertificateThumbprint`, or re-run the installer. TLS errors are logged at debug level; use `--diagnostics`. |
| "credential REJECTED" (`exit 3`) | Device was disabled/revoked, or the secret was rotated elsewhere. Create a re-enroll token (Devices -> Re-enroll) and run `--enroll`. |
| Dashboard shows OFFLINE | No heartbeat for > 5 min: PC off/asleep, service stopped (`Get-Service NISSMonitorService`), network blocked. SERVICE_STOPPED means a graceful stop was reported. |
| Dashboard shows WARNING | Heartbeat delayed > 60 s (busy network/PC). Persistent: check clock skew and the queue depth in Diagnostics. |
| "Session agent missing" alert | The user is logged in but `NISSMonitor.Session.exe` is not reporting. The service relaunches it within ~15 s (needs LocalSystem). Check `%LOCALAPPDATA%\NISS\Session\session.log`; AV may be blocking it. |
| No current application / idle data, but the device is online | Session process not running (see above), or `captureWindowTitles` is off (then only the process name is shown). |
| Queue keeps growing (`AGENT_BACKLOG_WARNING`) | Server unreachable or rejecting uploads. `--status` shows last error. The queue is durable and drains automatically, oldest first; it is capped (default 1,000,000 events; low-value telemetry is dropped first). |
| Dead-lettered events > 0 | The server permanently rejected them (validation). Details are in the agent log (`permanently rejected`). They no longer block the queue. |
| Timestamps look wrong | Compare `clockOffsetMs` in Diagnostics. Fix NTP on the PC; agents correct by the measured offset but a huge drift should be fixed at source. |
| USB events missing | USB monitoring needs WMI. `Get-Service winmgmt`; policy `usbMonitoringEnabled`. Devices already attached at agent start are the baseline (only later arrivals/removals are events). |
| Short-lived processes missing | Process monitoring samples every 2 s (`Agent:Monitoring:ProcessPollSeconds`). |
| Installer: "Windows build not supported" | Needs Windows 10 1809 / Server 2019 x64 or newer. |
| Installer fails at `sc create` | Not elevated, or the service already exists in a delete-pending state: reboot, then re-run. |
| Service won't start | `Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='NISS Monitor Service'} -MaxEvents 20`. Common: invalid `agentsettings.json` (fix JSON), data folder permissions (run `NISSAgent.exe --repair`). |
| Server: `Database migration failed` | Wrong connection string/password (`-DbPassword`), role lacks rights, or PostgreSQL not running. |
| Server: certificate error at startup | `Server:Certificate:Path`/`Password` wrong. Re-run `New-NissServerCertificate.ps1 -Force`. |
| Dashboard: "Cannot reach NISS server" | Open `https://<central-pc>:8443/api/v1/health` once in that browser and accept/trust the certificate (`Trust-NissCertificate.ps1`), and check the firewall. |
| Dashboard shows data but does not update live | SignalR/WebSocket blocked by a proxy; the status pill shows "reconnecting". Check that `https://<central-pc>:8443/hubs/monitor` is reachable. |

## Useful commands

```powershell
Get-Service NISSMonitorService | Format-List *
sc.exe qfailure NISSMonitorService            # recovery actions
Get-Process NISSMonitor.Session | Select Id, SessionId
NISSAgent.exe --diagnostics C:\temp\niss-diag.json
Get-Content 'C:\ProgramData\NISS\Agent\Logs\agent-*.log' -Tail 100
```

Support bundle: the dashboard's **Download Diagnostics** (admin) or `NISSAgent.exe --diagnostics` (contains a redacted log tail, queue stats, heartbeat history - no secrets, no monitored content).
