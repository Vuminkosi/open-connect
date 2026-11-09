# InMemory sample (`samples/InMemory`)

> **NON-DURABLE — dev only.** The EF Core InMemory provider keeps all state in process
> memory (`ef_inmemory_not_durable` semantics). Everything is lost on process exit.
> Never point this sample at production data, never use it for durability, outbox, or
> lease claims beyond local smoke checks. Owned by PERSIST-06-SAMPLE-BASE.

Dev-only host: its own `InMemorySampleDbContext`, exactly one provider
(`Microsoft.EntityFrameworkCore.InMemory`), `ApplyOpenConnectModel`, and
`Consistency = DevelopmentOnly` (durable mode would fail closed at startup).

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_INMEMORY_DBNAME` | InMemory store name | `openconnect-inmemory-sample` |

## Startup commands (from the repository root)

```powershell
# Dev-only smoke check (in-process, nothing written to disk)
dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- run

# Verify: model validation + in-process reachability
dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- verify
```

## Migrate / verify steps

- There is intentionally **no** `Migrations/` directory and no history file: the InMemory
  provider has no relational migration pipeline. The `migrate` verb is rejected by design
  (`sample_inmemory_no_migrations`, exit 2). Adding migrations here would be misleading.
- `verify` checks `CanConnect` (in-process) and lists the mapped entity set.
- Restart behavior: every restart starts **empty** — this is expected and must not be
  "fixed" with persistence.

## Ownership

- Own DbContext: `InMemorySampleDbContext` (`InMemorySampleDbContext.cs`).
- Own setup: no migrations, no history file (documented absence, not an omission).
- Receipt/command linkage (PERSIST gap G4): this sample exercises no receipt flows;
  receipt passthrough is **NOT VERIFIED** and no legacy receipt columns are invented.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/InMemory/OpenConnect.Samples.InMemory.csproj -c Release
dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- run
dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- verify
dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- migrate  # must exit 2
```
