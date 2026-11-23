# Upgrade guide

## Server

1. `.\NISS.Server.Stop.ps1`
2. Pull/copy the new version, then `.\NISS.Server.Start.ps1 -Rebuild` (builds, **applies migrations**, restarts).
3. Migrations are forward-only and idempotent (`deployment\database\schema.sql` is generated with `--idempotent`). Back up first:
   `pg_dump -Fc -h localhost -U niss niss_monitor > niss-backup.dump`.
4. Set `Monitoring:MinAgentVersion` (or Settings -> Health thresholds) to flag agents that must be upgraded (`AGENT_OUTDATED`).

The API is backwards compatible with older agents within the same `schemaVersion` (currently 1): new event types are additive.

## Agent

1. Build the new installer: `.\deployment\scripts\Build-Agent.ps1` (bumps nothing automatically - set `<Version>` in `Directory.Build.props` first).
2. Run the new `NISSMonitorSetup.exe` (or `Install.ps1`) on the PC. It stops the service and session agents, replaces the files, **keeps**
   `agentsettings.json`, the device identity, the queue and policy, re-applies the service configuration and starts the service again.
   No re-enrollment is needed.
3. On start the agent reports `AGENT_VERSION_CHANGED`; the dashboard's Overview shows the version distribution.
4. Roll out in waves: pilot group, then everyone. Devices that were offline upgrade when they next receive the package from your management tooling.

Rollback = install the previous installer; the local SQLite schema is forward compatible (additive) within a major version.

## Rotating secrets

* Device credentials: `NISSAgent.exe --rotate-credentials` (immediately invalidates the previous secret), or revoke + re-enroll from the dashboard.
* Server certificate: `New-NissServerCertificate.ps1 -Force`, restart the API, update the pin on agents (`agentsettings.json` or reinstall) before the old certificate is removed.
* JWT signing key: delete `Security:JwtSigningKey` (regenerated at start) - all admins must sign in again.
