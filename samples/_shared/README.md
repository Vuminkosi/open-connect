# Sample shared helpers (`samples/_shared`)

Provider-neutral composition helpers for the first-party EF Core samples. Owned by PERSIST-06-SAMPLE-BASE.

## What lives here

- `OpenConnect.Samples.Shared` class library:
  - `SampleEnvironment` — resolves SQLite file paths / InMemory names from the environment.
  - `SampleHost` — generic-host builder (JSON + env vars). Registers **no** provider.
  - `SampleVerify` — provider-neutral checks (`CanConnect`, mapped-table listing,
    applied-vs-pending migrations via the EF Core API).

## Rules

- No provider packages referenced here (OC-PERSIST-001 rule 2).
- No provider-specific SQL anywhere in this library.
- No migrations here. Each sample owns its `Migrations/` directory and history table.
- No secrets in code, logs, or settings. Connection strings come from the environment
  and are never logged.

## Environment variables (per sample)

| Sample | Variable | Default |
| --- | --- | --- |
| Minimal | `OPENCONNECT_MINIMAL_SQLITE_PATH` | `<appdir>/minimal-sample.db` |
| Sqlite | `OPENCONNECT_SQLITE_PATH` | `<appdir>/sqlite-sample.db` |
| InMemory | `OPENCONNECT_INMEMORY_DBNAME` | `openconnect-inmemory-sample` |

Receipt/command linkage (PERSIST gap G4): none of the samples exercise receipt flows.
Receipt passthrough is **NOT VERIFIED**; if a sample ever needs receipt columns it must
use OperationalRuns-shaped storage, never invented legacy columns.
