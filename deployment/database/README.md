# Database deployment notes

* `schema.sql` - the complete schema as an **idempotent** PostgreSQL script (generated with
  `dotnet tool run dotnet-ef migrations script --idempotent`). It can be run repeatedly with `psql -f schema.sql`; every step checks
  `"__EFMigrationsHistory"`, so it also upgrades an older database. Regenerate it whenever a migration is added.
* The API applies migrations itself when `Database:ApplyMigrationsOnStartup` is `true` (default) and supports
  `NISS.Api.exe --migrate` (apply and exit). Use `schema.sql` instead when the application account must not own the schema.

## Create the database and the service account

```sql
CREATE ROLE niss_monitor LOGIN PASSWORD '<generate a long random password>';
CREATE DATABASE niss_monitor OWNER niss_monitor ENCODING 'UTF8';
```

Put the connection string in `serversettings.local.json` (git-ignored, never in `serversettings.json`):

```json
{ "Database": { "ConnectionString": "Host=127.0.0.1;Port=5432;Database=niss_monitor;Username=niss_monitor;Password=..." } }
```

or in the environment: `NISS_Database__ConnectionString`.

## Apply the script by hand

```powershell
psql -h 127.0.0.1 -U niss_monitor -d niss_monitor -v ON_ERROR_STOP=1 -f schema.sql
```

If the application role is not the owner, set `Database:ApplyMigrationsOnStartup` to `false`.

## Operations

* Back up with `pg_dump -Fc niss_monitor`. The database contains password hashes, device-secret hashes and the generated JWT signing
  key (`server_settings.jwt_signing_key`): protect backups like credentials. Restoring a backup on another server keeps all tokens valid.
* Disk growth is dominated by `event_ingestion` (raw JSON of every event), `process_events`/`process_sessions` and `input_activity`.
  Tune the retention windows under *Settings -> Retention* (or `Retention` in `serversettings.json`).
* Full description of tables, indexes and retention: `docs/database.md`.
