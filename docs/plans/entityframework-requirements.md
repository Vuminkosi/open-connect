# OpenConnect — Pluggable Entity Framework Core Persistence Requirements

**File:** `entityframework-requirements.md`  
**Version:** 1.0-draft  
**Date:** 2026-11-07  
**Status:** Proposed normative requirements; not proof of implementation  
**Companion:** `entityframework-spec.md`  
**Audience:** Codex, Muse, DeepSeek, maintainers and application integrators

## 0. Product intent and normative language

OpenConnect is a .NET-first, privacy-first reusable AI framework, not an application. Its persistence implementation MUST integrate with developers' **existing EF Core database configuration** instead of imposing PostgreSQL, creating a second database, or requiring them to define `DbSet<T>` properties. The framework retains its provider-independent `ISessionStore`, `ITaskStore`, `IEventJournal`, `IOutboxStore`, approvals, leases, privacy-vault and operational-run contracts. The EF Core adapter is optional and selected only by explicit DI registration.

- **MUST / SHALL:** required to claim feature support and pass release gates.
- **SHOULD:** preferred default; deviations require a documented architectural decision and tests.
- **MAY:** optional extension.
- **Normative precedence:** OpenConnect security, agent-identity, operational scheduling and event-contract requirements remain in force. This document governs EF Core adapter registration, schema, migrations and transactional guarantees where earlier documentation prescribed PostgreSQL specifically.
- **Traceability:** `OC-EF-001`–`OC-EF-096`; work packages `EF-IMP-001`–`EF-IMP-016`; tests `EF-AT-001`–`EF-AT-030` in companion spec. Mark status only after actual tests.
- **No invented functionality:** All example APIs in these documents are **target contracts** and must be compiled against the chosen supported EF Core version before describing them as shipped.

## 1. Architectural invariants

**OC-EF-001** `OpenConnect.Abstractions`, default agent registration, HTTP/WS clients, MCP and Navigator MUST NOT depend on EF Core or any particular provider; only the optional `OpenConnect.Persistence.EFCore` package introduces EF Core dependencies.

**OC-EF-002** `AddOpenConnect()` MUST NOT create tables, open connections, execute migrations, register a database provider, install NuGet provider packages or add a second database.

**OC-EF-003** The intended primary developer API is:

```csharp
builder.Services
    .AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
    {
        options.Schema = "openconnect";
        options.Migrations = OpenConnectMigrationMode.Manual;
    });
```

`ApplicationDbContext` is already registered by the application using its chosen provider. This API SHALL register EF-backed OpenConnect stores but not override or recreate the developer's provider configuration.

**OC-EF-004** Entities MUST be discoverable through `modelBuilder.ApplyOpenConnectModel()` in `OnModelCreating`. Consumers MUST NOT be forced to declare `DbSet<SessionEntity>`, `DbSet<TaskEntity>`, etc. Existing host business entities and mappings remain untouched.

**OC-EF-005** The same **connection string and database** are the usual configuration; a separate `OpenConnectDbContext` and/or separate physical database MAY be configured explicitly. This choice MUST not be inferred from an arbitrary connection string string comparison.

**OC-EF-006** Prefer isolated tables within SQL Server/PostgreSQL schema `openconnect`; SQLite and InMemory use prefixed logical tables/entity names because schema support differs. Entity mapping names MUST be consistent across EF model, generated migrations and runtime repositories.

**OC-EF-007** No backend may advertise durable outbox guarantees without real transactional atomicity, concurrency control, recovery and tested isolation semantics.

**OC-EF-008** `InMemory` is explicitly **development/test-only** with no durable, multi-process, transactional-outbox, distributed-lease or crash-recovery guarantees. An app requiring operational-agent leases/durable outbox MUST fail production startup if InMemory is selected.

**OC-EF-009** EF Core **provider portability** means supported contracts across the four tested provider families, not identical SQL, migrations or lock APIs. Provider-specific strategies may be selected via a validated dialect/capability abstraction without putting provider packages into the core EF adapter.

**OC-EF-010** All EF mappings, migrations, schema changes and migration execution are opt-in. Defaults MUST be safe for an existing live database.

## 2. Supported providers and external packages

| Provider | Developer-owned provider package | EF relation | Production durability | Schema strategy | Concurrency strategy |
|---|---|---|---|---|---|
| InMemory | `Microsoft.EntityFrameworkCore.InMemory` | Non-relational fake | **No** | Logical entity mapping | Single-process development behavior only |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` | Relational | Yes, tested | `openconnect` schema | App-managed concurrency token + provider-appropriate transactional claim |
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | Relational | Yes within single-node/writer limits | `oc_` table prefix, no schema | App-managed concurrency token, serialized writers/retry busy |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` | Relational | Yes, tested | `openconnect` schema | App-managed concurrency token + provider-appropriate transactional claim |

**OC-EF-011** `OpenConnect.Persistence.EFCore` MUST NOT reference a concrete `UseSqlServer`, `UseSqlite`, `UseNpgsql` or `UseInMemoryDatabase` extension, include provider package references, or automatically resolve/install missing packages.

**OC-EF-012** Developers choose the provider in their normal `AddDbContext<TContext>`/`AddDbContextFactory<TContext>` configuration. The package only consumes the configured context.

**OC-EF-013** Package and compatibility docs MUST declare the supported EF Core major version and a four-provider build/test matrix; integration with other EF Core providers is experimental until dialect/capability tests pass.

**OC-EF-014** SQL Server/PostgreSQL require actual running database integration tests, not simulated EF InMemory tests. SQLite file-based and in-memory relational SQLite are separate tests.

**OC-EF-015** Provider-specific optimizations MUST be isolated behind dialect interfaces or user-installed provider-specific adapter packages if direct provider dependencies are necessary. Public EF stores must continue using common abstractions.

## 3. Integration and configuration

**OC-EF-016** `AddOpenConnectEntityFramework<TContext>(Action<OpenConnectEfOptions>? configure)` MUST require `TContext : DbContext` and validate it is registered and has the OpenConnect entity model installed. Missing model integration fails with an actionable error, not an automatic mutation of a precompiled model.

**OC-EF-017** `ApplyOpenConnectModel(ModelBuilder, ...)` SHALL register all required entities with public model-registration extension and internal implementations, supported keys, indexes, max lengths, concurrency and required provider-specific schema/table conventions. No public `DbSet<>` property interface is required.

**OC-EF-018** `OpenConnectDbContext` SHALL be offered as an alternative to modifying an existing context. It can use any application-supplied EF provider/connection string, including the same physical database. Registering it MUST NOT silently bootstrap a database.

**OC-EF-019** Typed options MUST be exposed for `OpenConnectMigrationMode`, `OpenConnectEfIntegrationMode`, `OpenConnectOutboxClaimStrategy`, `OpenConnectPersistenceConsistency`, and `OpenConnectSchemaValidationMode`, with validated string wire representation where bound from configuration.

**OC-EF-020** Default migration mode MUST be `Manual`; no call to `Migrate`, `EnsureCreated`, `EnsureDeleted`, DDL or provider-specific schema creation may occur from `AddOpenConnectEntityFramework` alone.

**OC-EF-021** `None` mode means neither schema creation nor migration apply and only explicit model/runtime validation if enabled. `Manual` means the application/deployer runs migration commands/scripts; `ApplyOnStartup` is a separately explicit opt-in for a dedicated OpenConnect context only and must coordinate startup concurrency (or be denied when unsafe). In a shared application context, this option MUST be rejected rather than migrating unrelated app tables.

**OC-EF-022** Safe default migration workflow for existing context: add `ApplyOpenConnectModel` to host `OnModelCreating`; generate migration in host's migration project; review/apply migration using application's deployment process. Migrations must create only intended new tables and never drop/change unrelated business tables.

**OC-EF-023** For dedicated context, users can manage a provider-compatible, independently versioned migration assembly and migration history table. One monolithic compiled set of SQL Server migrations MUST NOT be assumed portable to SQLite/PostgreSQL; provide provider-specific migration guidance and examples.

**OC-EF-024** Existing-context migration history remains application-owned; dedicated OpenConnect context history should use e.g. `__OpenConnectMigrationsHistory` and `openconnect` schema where supported, configured via provider-specific options. Never edit migration history automatically from the EF adapter.

**OC-EF-025** Model cache key MUST include mapping-shaping options (schema, prefix, provider family) where a single context type can be used with multiple option sets in the same process. Design-time and runtime model must match; otherwise reject divergent configuration.

**OC-EF-026** All public options MUST be validated using .NET Options pattern, with startup validation; arbitrary client/agent/YAML instructions cannot change provider, schema, migration policy or table names.

**OC-EF-027** Default table naming SHOULD be predictable, e.g. `Sessions`, `Tasks`, `Turns`, `Events`, `Outbox`, `Leases`, `Approvals`, `PrivacyTokens`, `OperationalRuns`; SQLite maps with prefix `oc_`, such as `oc_sessions`. Explicitly document aliases/mapping decisions; no table-name collisions with existing host tables.

**OC-EF-028** Schema customization is allowed only through strongly validated, fixed identifier options (not raw SQL). Reject SQL injection, reserved names, excessively long identifiers and configuration drift against applied schema.

## 4. Entity and database model

**OC-EF-029** Support at least these entities, leaving optional modules to register their extensions: sessions, tasks, turns, events, outbox messages, outbox delivery status/leases, operational runs/leases, approvals, privacy tokens, and schema/version metadata where needed.

**OC-EF-030** OpenConnect entities are internal implementation details by default and accessed by OpenConnect stores with `DbContext.Set<TEntity>()`. Public domain contracts remain separate from persistence records. Avoid forcing callers to know persistence entity classes or map their own `DbSet` properties.

**OC-EF-031** All tenant/owner-scoped tables MUST include tenant and relevant principal/workload scope columns, be indexed for access paths, and be queried under a server-resolved authorization context. No user-supplied tenant ID confers authority. Global query filters MAY provide defense-in-depth but cannot replace explicit authorization checks.

**OC-EF-032** Events MUST retain per-session monotonic sequence, event UUID/idempotency identifier, sanitized payload, timestamp and correlation. They MUST NOT store unmasked prompts or arbitrary tool contents by default.

**OC-EF-033** Tasks and turns MUST support unique scoped client request IDs, optimistic version/fence, cancellation/terminal outcomes and unambiguous replay.

**OC-EF-034** Outbox rows MUST track delivery status, retry count, next attempt, lease owner/id, lease expiry, fencing version, payload digest, event id and safe failure code. Never persist broker credentials or unsanitized payloads.

**OC-EF-035** Privacy token mappings MUST be encrypted with keys controlled by approved .NET vault/key services (local protected key or optional Infisical KMS) and never expose plaintext through relational queries, logs or migration seeds. A database provider is NOT an encryption provider.

**OC-EF-036** Approved operational-agent definitions/runs, schedules and leases use dedicated constrained workload identity, not an interactive user token. Run leases and task outcomes obey the same provider-specific concurrency guarantees.

**OC-EF-037** Schema naming, collations, indexing, key lengths, blob types, date/time types and identifier length MUST be tested for each provider. Avoid relying on automatically generated rowversion/sequence support unavailable in SQLite.

## 5. Transactional outbox and durability

**OC-EF-038** When using one shared `ApplicationDbContext` instance, OpenConnect changes and application changes MAY participate in the **same EF Core transaction**, provided they are persisted within a defined transaction boundary and commit owner. The framework MUST expose a unit-of-work API and not call `SaveChangesAsync` or `CommitAsync` prematurely on behalf of a caller-owned transaction.

**OC-EF-039** Atomic persistence MUST cover accepted task / session mutation / appended committed event / enqueued outbox message in one relational transaction for durable mode. The exact trigger for publishing MUST be a committed outbox record—not an in-memory agent event callback.

**OC-EF-040** If application business data and OpenConnect outbox are in different DbContexts, even with identical connection strings, atomic commit is NOT guaranteed until their connection/transaction is deliberately shared with provider support. If separate physical databases are used, the feature MUST NOT claim a single-database ACID transaction; use a documented eventual consistency/compensating workflow instead.

**OC-EF-041** Support `IExecutionStrategy` for retriable transient database failures with correctly scoped transaction delegate and idempotency. Avoid nested implicit transactions that conflict with a caller-managed transaction; respect cancellation and prevent retries of already-completed non-idempotent external actions.

**OC-EF-042** Outbox dispatcher MUST not hold a DB transaction open during network publication. Claim/lease in short transaction -> publish to NATS -> confirm broker ACK -> short transaction mark delivered, with idempotent retry. If crash occurs after publish before delivered marking, duplicate broker events are expected and MUST be deduplicated downstream.

**OC-EF-043** Claims MUST be safe with multiple host replicas: use optimistic compare-and-swap/fenced lease or validated provider-specific claim strategy. No reliance on `SELECT ... FOR UPDATE SKIP LOCKED` across all providers.

**OC-EF-044** Lease acquisition, heartbeat and completion MUST compare fencing/version and reject stale owners. Operational agents and worker dispatch use the same platform primitives where appropriate.

**OC-EF-045** SQLite mode MUST document single-writer limitations, busy timeout/retry bounds and recommended limits for concurrent dispatchers. Support single-node/small deployment; distributed multi-writer guarantees are unavailable unless proven by tests and configuration.

**OC-EF-046** InMemory mode MUST NOT use real outbox dispatcher success semantics or pretend atomic commit. Offer a deterministic in-process test transport and explicit non-durable status; reject operational durable mode.

**OC-EF-047** Outbox processing failures must be retried with exponential backoff/jitter, bounded attempts, visibility timeout and quarantined terminal states, with safe diagnostics. Support maintenance repair/requeue through authorized operator API.

**OC-EF-048** Delivery ordering is guaranteed only for the specifically defined scope (e.g. per-session sequence) by the database journal; NATS cross-subject/consumer scheduling order is not a substitute for the journal's session sequence.

**OC-EF-049** Explicitly define transaction boundaries for privacy-token creation and consent/approval records; protected data may not be dispatched externally before required persisted privacy state is committed.

**OC-EF-050** Event and outbox deletes/retention must honor session/legal/privacy policies. Retries must not resurrect deleted sessions or republish revoked payload references; use versioned tombstones/revocation checks.

## 6. Service lifecycle, schema validation and migrations

**OC-EF-051** Adapter registration MUST be idempotent where repeated with identical settings, and MUST reject ambiguous registrations with different contexts, providers or schema configurations unless a named-store feature is implemented.

**OC-EF-052** Host startup MUST validate required model entity types and table mappings, relational capability when durability is required, and optionally the database schema version/readiness. Startup validation must be configurable to avoid connecting merely to build a container or generate tooling.

**OC-EF-053** No package-level automatic migrations, no `EnsureCreated` shortcut for shared databases, and no database deletion in any production code path.

**OC-EF-054** When `ApplyOnStartup` is explicitly supported in a **dedicated** context, coordinate migration concurrency across service replicas with a supported provider-compatible mechanism; otherwise reject startup and instruct operator to use a migration bundle/job. Never automatically migrate a shared application's context.

**OC-EF-055** A manual migration workflow MUST include a runnable CLI example (`dotnet ef migrations add` and reviewed `dotnet ef database update`/bundle) and must distinguish installing the adapter from executing schema changes.

**OC-EF-056** Migrations MUST preserve existing application tables, relationships, schema versions and history. Test before/after snapshots, foreign keys and unrelated business rows in all relational providers.

**OC-EF-057** Schema upgrades must be forwards-compatible for rolling deployments where practical, with safe fail/read-only mode on incompatible version and an explicit rollback/backup plan. No forced schema reset.

**OC-EF-058** Schema validation cannot simply assume one database schema contains only OpenConnect tables. Validate only owned objects; do not introspect business data unnecessarily.

## 7. Security, privacy and tenancy

**OC-EF-059** No raw sensitive values in logs, traces, query diagnostics or error messages. Enable EF Core sensitive-data logging only in controlled synthetic test hosts; never in production default.

**OC-EF-060** Connection strings/credentials must originate in application's secret configuration and MUST NOT be copied into `agent-runtime.md`, models, task payloads, NATS, docs examples or OpenTelemetry tags.

**OC-EF-061** SQL injection must be excluded by validated identifiers, parameterized queries and provider-specific SQL guards. Reject arbitrary schema/table name interpolation from client input.

**OC-EF-062** Privacy-token storage MUST use authenticated encryption before DB writes, scoped to tenant + principal/workload + session + turn and expiry. KMS/Infisical integrations remain optional and separate packages.

**OC-EF-063** All retry claims, event projections, sessions, approvals and deletions enforce tenant isolation and appropriate principal/workload permissions. Operational agents cannot inherit the latest interactive user's context.

**OC-EF-064** Bulk deletion, retention, migration tooling and administrative diagnostics require explicit authorization and no uncontrolled cross-tenant disclosure.

**OC-EF-065** EF Core concurrency conflicts must not leak entity values; map to stable typed errors and safe retries.

**OC-EF-066** A compromised NATS consumer, agent or browser must not be able to change EF store authorization scope or access the token vault by supplying a session ID.

## 8. API design and extension boundaries

**OC-EF-067** Public store interfaces remain generic and provider-independent; implementations of `IEventJournal`, `IOutboxStore`, `ITaskStore`, `ISessionStore`, `ILeaseStore`, `IApprovalStore` and `IPrivacyTokenVault` MUST be properly registered via a single adapter.

**OC-EF-068** An `IOpenConnectPersistenceUnitOfWork` (or equivalent) MUST expose atomic execution/transaction ownership semantics, not merely `SaveChangesAsync`. Distinguish `OwnedTransaction`, `AmbientSharedContext` and `ExternalTransaction` where required.

**OC-EF-069** Data transfer records and persistence entity types are separate. EF models do not become API contracts or require serialization to clients.

**OC-EF-070** Mapping extension accepts explicit options (`schema`, `table prefix`, `provider capability`) so shared-context and dedicated-context mappings behave identically for a given provider.

**OC-EF-071** No reflective, auto-discovered assembly scans that accidentally map unrelated host entities. OpenConnect mappings are registered by a closed explicit set of `IEntityTypeConfiguration<T>` types and optional modules registered explicitly.

**OC-EF-072** `OpenConnect.Persistence.EFCore` MUST NOT reference Blazor, browser, MAF provider adapters, NATS server SDK, Docker or Qdrant implementation packages. It may depend on common OpenConnect abstractions and EF Core relational APIs needed for supported relational behavior.

**OC-EF-073** A higher-level `.AddOpenConnectEntityFramework()` convenience API MAY register a dedicated context when developer explicitly supplies provider configuration; it MUST NOT guess a connection string/provider by constructing another context from an existing `DbContext`'s current connection.

**OC-EF-074** `IOpenConnectComponent` and Navigator client metadata remain unaffected by provider selection; only authenticated domain events and authorization context flow to client.

**OC-EF-075** Optional event subscribers such as QdrantVactor use committed outbox/JetStream notifications; they MUST NOT require scanning all `IEventJournal` sessions or injecting vectors into `EventRecord`.

**OC-EF-076** Support optional application-level usage through an explicitly registered shared transaction coordinator for business data + OpenConnect events; document ambient/interleaved `SaveChanges` behavior precisely.

## 9. Testing and acceptance

**OC-EF-077** Unit tests SHALL cover model registration without `DbSet` properties, typed options, schema/table name normalization, duplicate DI and missing model detection.

**OC-EF-078** Integration suite SHALL run SQLite in-memory relational + SQLite persistent file; SQL Server disposable test instance; PostgreSQL disposable instance; EF InMemory only for clearly labeled non-durable tests.

**OC-EF-079** Existing application DbContext with `Customers`/`Orders` must maintain rows and unrelated schema before and after adding OpenConnect mappings and migrations.

**OC-EF-080** Per-provider outbox concurrency/fault tests: claim race, lease expiration, fail during publish, duplicate NATS ACK, restart, stranded outbox and multiple hosts.

**OC-EF-081** Outbox write and task/session commit must prove transaction rollback and idempotency. Shared vs dedicated context transaction semantics must be separately demonstrated.

**OC-EF-082** SQLite specifically must prove safe high-contention behavior for documented supported profile, or be restricted to a smaller deployment profile.

**OC-EF-083** InMemory cannot pass transactional durability tests; startup must reject when durable production mode is required.

**OC-EF-084** Migrations tests must show: no registration-time DDL, correct `Manual` behavior, dedicated-only startup migration opt-in, host-business migration isolation, SQLite no-schema mapping and separate migration history.

**OC-EF-085** Provider-neutral contracts must be tested through the same test vectors against all relational adapters, not forked per-provider logic that diverges.

**OC-EF-086** Package consumer tests must reference only published/packed OpenConnect NuGet packages + one installed EF provider, and compile with user's DbContext without adding any OpenConnect `DbSet<>` properties.

## 10. Performance, operations and releases

**OC-EF-087** Configure batch size, dispatch concurrency, poll delay, lease duration, max retries, retention, table prefix/schema and query timeout with strongly typed options and safe bounds. No unbounded scans.

**OC-EF-088** Index session/event/outbox queries and keep payload sizes bounded. Large data uses authorized references to encrypted blobs, never unbounded event columns.

**OC-EF-089** OTel signals include DB duration, pending outbox age/count, publish lag, lease conflicts, retry/dlq, schema version and provider family, without connection-string/user payload labels.

**OC-EF-090** Core host works without the adapter if a different persistence implementation is registered. No startup error due simply to missing EF package when not selected.

**OC-EF-091** Document single-process vs multi-host suitability by provider; SQLite/InMemory not described as equivalent to SQL Server/PostgreSQL under distributed load.

**OC-EF-092** Provider package versions are controlled by the application and compatibility-tested, without forcing unneeded package installation or binary redistribution of database drivers.

**OC-EF-093** CI gates: model/contract tests, migration script inspect, SQLite/in-memory, SQL Server/PostgreSQL integration, fault injection, package-consumer smoke, security scan and retention tests.

**OC-EF-094** Release package is `OpenConnect.Persistence.EFCore` on NuGet; additional provider dialect packages are OPTIONAL only if implementation genuinely requires them and must never be transitively pulled into core.

**OC-EF-095** Release documentation provides exact `AddDbContext` and `AddOpenConnectEntityFramework` examples for all four providers, alongside manual migrations and transaction warnings.

**OC-EF-096** Release acceptance requires ALL critical EF-AT tests for included production providers; features not validated must be marked experimental and cannot be advertised with full durable outbox guarantees.

## 11. Required developer experiences

### 11.1 Existing SQL Server context (primary)

```csharp
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddOpenConnect();
builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
{
    options.Schema = "openconnect";
    options.Migrations = OpenConnectMigrationMode.Manual;
});

// One-time application source change:
// protected override void OnModelCreating(ModelBuilder modelBuilder)
// {
//     base.OnModelCreating(modelBuilder);
//     modelBuilder.ApplyOpenConnectModel();
// }
```

### 11.2 Existing PostgreSQL context

Same mapping + registration but the application's own provider configuration uses `UseNpgsql(...)`. No `OpenConnect.Persistence.Postgres` installation is required.

### 11.3 Existing SQLite context

Application uses `UseSqlite(...)`. `Schema = "openconnect"` is mapped to provider-safe table prefix `oc_` (or explicitly configured prefix); it MUST NOT silently pretend SQLite has a named relational schema.

### 11.4 InMemory development

Application uses `UseInMemoryDatabase(...)` and chooses `Migrations = None` with `Durability = DevelopmentOnly`. Developer documentation MUST show that this does not persist across restart and is not a transactional-outbox integration.

### 11.5 Dedicated context (optional)

Provide `OpenConnectDbContext` and a registration overload where the developer configures EF provider explicitly and the app may choose the same connection string or a separate database. No requirement to add OpenConnect entity configurations to `ApplicationDbContext` in this mode; cross-context business/outbox atomicity is **not automatic**.

## 12. Failure behavior and boundaries

| Condition | Required behavior |
|---|---|
| EF adapter not registered, other persistence registered | Core uses configured persistence; no provider packages pulled in |
| Existing DbContext not registered | Fail with `ef_context_missing` at startup |
| OpenConnect model not applied | Fail with `ef_model_missing`, include setup instruction |
| SQL Server/Postgres schema absent, Manual mode | Startup readiness/config warning; never auto-create |
| SQLite no named schema | Map prefixed tables; no schema DDL |
| DB unavailable | Accepted task not reported committed without database commit; bounded retry |
| Outbox crash after broker publish | Message redelivered, idempotent downstream processing |
| Shared context with external transaction | Respect caller commit owner, no premature publish or commit |
| Separate context with same connection string | Warn/no claim of atomic shared business transaction |
| Separate databases | Explicit eventual-consistency/compensation design; no ACID claim |
| InMemory in durable production mode | Reject startup |
| SQLite multiple writer contention | Bounded retries/lease handling, documented scale limits |
| Incompatible migrations | Fail safely; no destructive schema reset |
| Privacy vault unavailable | Block required protected transfer, never write plaintext |

## 13. Source and verification references

- EF Core model discovery: https://learn.microsoft.com/en-us/ef/core/modeling/entity-types
- EF Core transactions and cross-context sharing: https://learn.microsoft.com/en-us/ef/core/saving/transactions
- EF Core concurrency: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
- EF Core migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing
- EF Core separate migration projects: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects
- EF Core migration history: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/history-table
- SQLite limitations: https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- EF Core InMemory cautions: https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy

**Implementation agent must validate chosen EF Core release and provider APIs rather than assuming all provider-specific operations compile unchanged.**
