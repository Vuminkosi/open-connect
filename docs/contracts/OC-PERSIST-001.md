# OC-PERSIST-001 — Single Provider-Neutral EF Core Adapter

> Status: APPROVED by orchestrator 2026-11-09 (substance; experimental caveats in §5 stand). No implementation, test, migration, or provider-support claim is made. All behavior NOT VERIFIED until Codex EF-AT evidence exists.

## 1. Invariant (Codex §1 / §4 / §8)

One supported EF adapter — `OpenConnect.Persistence.EFCore` — over the application's own `DbContext` and provider. No Postgres-specific (or other provider-specific) persistence package in the supported architecture. Registration (`AddOpenConnectEntityFramework<TContext>`) never migrates; model (`ApplyOpenConnectModel`) never connects; shared-transaction atomicity holds only inside one explicit `DbContext`-instance unit-of-work with a single commit owner.

## 2. Rules

1. Single adapter: `OpenConnect.Persistence.EFCore` owns entities, mappings, stores, DI, transaction/outbox coordination; `Abstractions` has no EF dependency.
2. No provider in the adapter's dependency graph: EF base/Relational only; the app installs exactly one provider package.
3. No DDL on registration: default `Migrations.Manual`; `None` = validate-only; `ApplyOnStartup` valid solely for an explicit dedicated context with coordination, otherwise rejected (`ef_migration_not_allowed`).
4. Migrations are consuming-app-owned and manual: host `dotnet ef migrations add` → review SQL → deploy via app pipeline; runnable per-provider samples only; separate histories (dedicated: `__OpenConnectMigrationsHistory`).
5. No mandatory second database: same connection string/database is the usual case; dedicated context/database only by explicit configuration.
6. No assumed shared transaction: same connection string across two contexts is not atomic; cross-database is eventual-consistency/saga; repositories never `SaveChanges`/`Commit` under a caller-owned transaction.
7. Provider differences isolated: `Auto` → conservative compare-and-swap/fenced lease; `ProviderOptimized` only behind a verified dialect; unverified provider or durable-mode InMemory fails closed (`ef_provider_unsupported` / `ef_inmemory_not_durable`).

## 3. Acceptance IDs (one-line scopes)

- OC-PERSIST-001 — Adapter package carries no provider reference.
- OC-PERSIST-002 — Registration performs no connection, DDL, or migration.
- OC-PERSIST-003 — Missing model/provider fails closed with stable error before queries.
- OC-PERSIST-004 — Shared-context atomic task/session/event/outbox commit rolls back together.
- OC-PERSIST-005 — Multi-replica outbox claim delivers once; stale owner cannot complete.
- OC-PERSIST-006 — Manual migration sample adds only intended tables per provider.
- OC-PERSIST-007 — Clean consumer (adapter + one provider, no `DbSet` additions) runs task/outbox/replay.

## 4. Codex ID mappings (§§1–10)

- §1 Invariants → OC-EF-001–010; spec §§1,4,8.
- §2 Providers/packages → OC-EF-011–015; spec §§2,14.4.
- §3 Integration/config → OC-EF-016–028; spec §§4,5,7.
- §4 Entity model → OC-EF-029–037; spec §§5,6.
- §5 Outbox/durability → OC-EF-038–050; spec §§8,9.
- §6 Lifecycle/migrations → OC-EF-051–058; spec §10.
- §7 Security/privacy/tenancy → OC-EF-059–066; spec §12.
- §8 API boundaries → OC-EF-067–076; spec §§1,7,12.
- §9 Testing/acceptance → OC-EF-077–086; spec §14 (EF-AT-001–030).
- §10 Perf/ops/release → OC-EF-087–096; spec §§13–15.

## 5. Open decisions (explicit, unresolved)

1. 4-table gap: `OperationalRuns`, `NativeAgentSessions`, `PublicSessionMappings`, `ApprovalNonces` are EFCore-only with no legacy `oc_*` counterpart — RETAIN as new capability; legacy `command_id`/receipt backfill plan still missing (see `PERSIST-parity-matrix.md` G1–G4).
2. `SKIP LOCKED` dialect: PostgreSQL `FOR UPDATE SKIP LOCKED` provider-optimized claim is designed (spec §9.2) but behavior on Npgsql NOT VERIFIED; `Auto`/CAS remains the safe default until the provider matrix passes.
3. net9/net10 support: supported EF Core major version + four-provider matrix (SQL Server/PostgreSQL/SQLite-file/InMemory-dev) undeclared and untested — experimental until EF-AT-028/030 pass; failing providers must be excluded from advertised support.

## 6. Sources / evidence

- `docs/plans/entityframework-requirements.md`, `docs/plans/entityframework-spec.md`, `docs/plans/PERSIST-parity-matrix.md` (read-only).
- `src/OpenConnect.Persistence.EFCore/*.csproj`, `DependencyInjection/*`, `Providers/*` (read-only).
- Branch/HEAD per orchestrator-established baseline (agents-1 @ 0b9309f, clean tree).
