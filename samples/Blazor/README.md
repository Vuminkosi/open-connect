# Blazor sample (`samples/Blazor`)

A runnable Blazor Server page proving provider-neutral consumption of the hardened
EFCore adapter against the simplest store (SQLite file). The single screen is organized
around user jobs — not a data dump:

1. **Deliveries waiting** — pending outbox items via `IOutboxStore.GetPendingAsync`;
   confirm one as handled (`MarkDeliveredAsync`) or refresh the queue.
2. **Session history** — replay a session's recorded steps via `IEventJournal.ReplayAsync`
   from a pasted session reference, oldest first.
3. **Local store health** — reachability, stored record-type count, and applied vs.
   pending updates through the provider-neutral EF Core API, with an idempotent
   "apply pending updates" action (`MigrateAsync` over this sample's own history).

Same owned-migration discipline as the Minimal console sample: own `BlazorDbContext`,
own migrations + history table (`__BlazorMigrationsHistory`) in this sample's own
SQLite file. The sample seeds no data.

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_BLAZOR_SQLITE_PATH` | Absolute SQLite file path | `<appdir>/blazor-sample.db` |
| `ASPNETCORE_ENVIRONMENT` | Host environment | `Production` |

No other configuration is required. Connection values are never logged.

## Startup commands (from the repository root)

```powershell
# 1. Generate this sample's own migration (verification lane owns execution).
#    Creates Migrations/<timestamp>_Initial.cs (+ Designer/snapshot) scoped to
#    BlazorDbContext with history table __BlazorMigrationsHistory.
dotnet ef migrations add Initial `
  --project samples/Blazor/OpenConnect.Samples.Blazor.csproj `
  --context OpenConnect.Samples.Blazor.BlazorDbContext `
  --output-dir Migrations

# 2. Run the sample (startup applies this sample's own migrations, idempotent).
dotnet run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj

# 3. Open the work overview (default profile): http://localhost:5199
```

## Migrate / verify steps

- Startup calls `Database.MigrateAsync()` through this sample's pipeline only
  (`BlazorDbContext`, `MigrationsAssembly` = this sample, dedicated history table).
- Restart-idempotent: re-running against an existing database file is a no-op for
  already-applied migrations.
- Manual check without the UI: point `OPENCONNECT_BLAZOR_SQLITE_PATH` at the same
  file and run the Minimal sample's `verify` verb to confirm zero pending migrations.

## Ownership

- Own DbContext: `BlazorDbContext` (`BlazorDbContext.cs`).
- Own migrations: `Migrations/` — created ONLY via the `dotnet ef migrations add`
  command above (hand-authored migrations prohibited).
- Own history: `__BlazorMigrationsHistory` in this sample's database file only.
- Design-time factory: `BlazorDbContextFactory` (supports `dotnet ef migrations add`).
- No `src/**` changes; no shared components, styles, or tokens added or changed;
  `wwwroot/sample.css` and the `*.razor.css` files are sample-local.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet ef migrations add Initial --project samples/Blazor/OpenConnect.Samples.Blazor.csproj --context OpenConnect.Samples.Blazor.BlazorDbContext --output-dir Migrations
dotnet build samples/Blazor/OpenConnect.Samples.Blazor.csproj -c Release
dotnet run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj
# Browse http://localhost:5199: confirm queue empty state, store "Ready",
# history validation message for a blank reference; restart and re-open (idempotent).
```
