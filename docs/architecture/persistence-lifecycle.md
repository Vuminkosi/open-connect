# OpenConnect Persistence Operational Lifecycle Guide

This operational architecture guide documents backup/restore, forward-compatible migrations, destructive retention and purge, legal hold, and rolling upgrade operational policies for OpenConnect EF Core relational persistence (PostgreSQL, SQL Server, and SQLite).

---

## 1. Backup and Restore Procedures

### 1.1 Consistency & Point-in-Time Recovery
- **Single-Database Atomicity**: OpenConnect relational persistence (`OpenConnect.Persistence.EFCore`) commits session state, turns, events, and transactional outbox entries in single relational database transactions.
- **Backup Requirements**:
  - Always perform atomic point-in-time relational backups (e.g., PostgreSQL `pg_dump` / WAL archiving, SQL Server Full + Transaction Log backups, SQLite Online Backup API `VACUUM INTO`).
  - Never back up outbox tables separately from session and event journal tables.
- **Pre-Restore Drainage & Post-Restore Reconciliation**:
  - Before restoring a database snapshot, drain or halt active outbox dispatchers and background workers.
  - Upon restore from an earlier snapshot, outbox entries with `status = 'pending'` or `status = 'claimed'` that were already delivered to the message broker (e.g. NATS JetStream) will be redelivered. All downstream subscribers must rely on idempotent deduplication keyed on the immutable `EventId`.
  - Stale execution leases with `ExpiresUtc < UtcNow` are automatically reclaimed or expired; no manual lease cleanup is required.

---

## 2. Forward-Compatible Migration Strategy

### 2.1 Principle of Non-Breaking Additive Schema Evolution
- **Additive Migrations**: Schema updates must strictly add new tables or nullable/defaulted columns. Never rename or drop columns in the same release that introduces their replacements.
- **Provider Parity**:
  - PostgreSQL / SQL Server: Map to the dedicated schema `openconnect` (or host-configured schema).
  - SQLite: Map table names with the `oc_` prefix (e.g., `oc_sessions`, `oc_outbox`) without pretending named schema support exists.
- **Manual Migration Verification**:
  - Migrations default to `OpenConnectMigrationMode.Manual`.
  - Automatic `ApplyOnStartup` is strictly forbidden on shared application `DbContext` instances to prevent accidental execution of unrelated business migrations.
  - Generate and review SQL scripts (`dotnet ef migrations script`) before running `dotnet ef database update` in staging and production environments.

---

## 3. Destructive Retention, Purge, and Legal Hold

### 3.1 Bounded Batch Tenant-Scoped Purge
- **No Unconditional Cascade Delete**: Long session trees (sessions -> turns -> events -> approvals) must never be purged using uncontrolled cascading deletes, which cause long-lived locks and transaction log exhaustion.
- **Tenant Isolation**: All purge queries must enforce `TenantScope` filters.
- **Bounded Batches**: Purge workers operate in small, bounded transaction batches (e.g., 50–200 sessions per batch) with configurable sleep intervals to minimize lock contention.
- **Vector & Projection Invalidation**: Deleting session data emits audit tombstone events triggering deletion of derived vector embeddings, semantic caches, and ephemeral storage.

### 3.2 Vault Token Scrubbing
- `IPrivacyTokenVault.DeleteExpiredAsync`: Expired privacy tokens are deleted based on `ExpiresUtc < UtcNow` regardless of whether the overarching session remains retained.
- Raw PII is never stored in plaintext in the database or logs; all token mappings store AES-256-GCM ciphertext.

### 3.3 Legal Hold Freeze
- Sessions, tasks, or tenants flagged with an active `LegalHold` policy must be bypassed by automated retention and purge jobs.
- The purge worker checks legal hold status prior to evaluating session expiration dates.

---

## 4. Rolling Upgrade & Multi-Instance Restrictions

### 4.1 N / N+1 Version Compatibility
- During a rolling deployment, nodes running version $N$ and version $N+1$ may concurrently access the database.
- Schema changes for version $N+1$ must be deployed and verified before rolling new application binaries.
- Old nodes must continue to function correctly when nullable columns or new tables exist.

### 4.2 Distributed Lease Fencing
- Outbox claim dispatchers and operational scheduler workers employ monotonic fencing versions (`FencingVersion`).
- When a lease is renewed or taken over by a failover replica, `FencingVersion` increments monotonically. Any delayed write from an evicted or stale node is rejected via optimistic concurrency checks.

### 4.3 SQLite Concurrency Constraints
- SQLite is supported for single-process local and development configurations.
- Concurrent writes to a single SQLite database file from multiple OS processes will encounter `busy` / `locked` states. Applications using SQLite must configure `BusyTimeout` and serialize write transactions or use WAL mode with single-writer coordination.
