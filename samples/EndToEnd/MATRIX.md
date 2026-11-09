# PERSIST-12 E2E Harness - Contract/Acceptance Matrix (samples/EndToEnd)

Owner: TASK PERSIST-12-E2E-HARNESS. Harness only: matrix definition plus
deterministic PowerShell runner scripts using the repo-approved dotnet CLI.
No new test framework, no scheduler/daemon, no product changes, no MAF changes.

- Approved contracts: OC-PERSIST-001 plus orchestrator gap dispositions:
  RETAIN all four gaps. Consequences enforced by this harness:
  - G4 backfill is future work: this harness MUST NOT invent backfill SQL,
    backfill verbs, or backfill assertions.
  - Receipt carrier is UNDECIDED: this harness MUST NOT invent receipt linkage,
    receipt columns, or receipt assertions. The Navigator case exercises only
    the implemented lease plus outbox-relay paths (that slice implements no
    receipt members by contract).
  - Outbox/resolution paths only as implemented: verbs exercised are exactly
    the verbs each sample implements (migrate/run/verify; Navigator
    migrate/lease-demo/outbox-relay-demo/verify; PostgreSql parity offline).
- Canonical: C:\DevOps\open-connect\worktrees\agents-1, branch agents-1.
- Baseline HEAD verified read-only before authoring:
  ad65ad8e354e4dc431cec683c2ca6bd540f83884.
- Prohibited (never touched by this harness): src, other samples, slnx, props,
  workflows, CI, production databases/credentials, docker pull, Testcontainers
  image fetches. A cache-missed image means that cell is NOT VERIFIED, never
  a pull.

## Conventions

- Case exit codes: 0 = PASS, 1 = FAIL, 3 = NOT VERIFIED (missing fixture,
  gated, skipped). Orchestrator Run-E2E.ps1 exits 0 when no cell FAILs and 1
  when any cell FAILs. A dotnet step that exceeds its timeout is killed and
  reported with exit 124.
- Bounded timeouts: 300 s per dotnet step (-TimeoutSec), 1200 s per case
  process (-CaseTimeoutSec), 300 s Blazor startup poll
  (-BlazorStartupTimeoutSec). A failed step is recorded; the case still runs
  its remaining steps so one log shows every failure, then exits non-zero.
- Isolation: each run creates a fresh TEMP oc-e2e run folder with one SQLite
  file per file-backed sample (minimal-sample.db, sqlite-sample.db,
  blazor-sample.db, navigator-sample.db), passed ONLY via that sample's own
  env var (OPENCONNECT_MINIMAL_SQLITE_PATH, OPENCONNECT_SQLITE_PATH,
  OPENCONNECT_BLAZOR_SQLITE_PATH, OPENCONNECT_NAVIGATOR_SQLITE_PATH).
  InMemory uses a unique database name per run; no file. Nothing is shared
  with production or across samples.
- Secrets: connection strings/credentials flow only from pre-existing process
  environment into child processes and are NEVER printed. Harness console
  tails are scrubbed of password/passwd/pwd/secret/token/connection-string
  assignment lines.
- Cleanup: each case removes its temp DB file (E2E-CLEANUP line); the
  orchestrator removes the whole temp root (logs included) and reports
  tempRoot=removed/absent-already/remove-failed.

## How to run (from the repository root)

    powershell -NoProfile -ExecutionPolicy Bypass -File samples/EndToEnd/Run-E2E.ps1

One case only (Navigator example; each case is self-contained):

    powershell -NoProfile -ExecutionPolicy Bypass -File samples/EndToEnd/cases/Run-Navigator.ps1 -RepoRoot . -DbFile $env:TEMP\navigator-sample.db -LogDir $env:TEMP\oc-e2e-logs

## Matrix (commands run from the repository root)

Working directory for every dotnet step: the repository root.

| Cell | Sample / verb(s) | Exact command(s) | Env (names only; values never logged) | Expected observable | Result |
|---|---|---|---|---|---|
| E2E-01 | Minimal run | dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- run | OPENCONNECT_MINIMAL_SQLITE_PATH = temp file | exit 0, Run check complete. No data seeded. | PASS (exit 0) |
| E2E-02 | Minimal verify | dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- verify | same temp file | exit 0, Verify OK, none pending | PASS (exit 0) |
| E2E-03 | Sqlite run | dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- run | OPENCONNECT_SQLITE_PATH = temp file | exit 0, Run check complete. No data seeded. | PASS (exit 0) |
| E2E-04 | Sqlite verify | dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- verify | same temp file | exit 0, Verify OK, none pending | PASS (exit 0) |
| E2E-05 | InMemory run | dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- run | OPENCONNECT_INMEMORY_DBNAME = unique per run | exit 0, NON-DURABLE warning printed | PASS (exit 0) |
| E2E-06 | InMemory verify | dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj -- verify | same db name | exit 0, model validates in-process | PASS (exit 0) |
| E2E-07 | Blazor HTTP smoke | start dotnet run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj --no-launch-profile, poll GET / | OPENCONNECT_BLAZOR_SQLITE_PATH = temp file, ASPNETCORE_ENVIRONMENT = Development, ASPNETCORE_URLS = loopback port | HTTP 200 plus What-needs-attention marker, then process stopped | PASS (exit 0; HTTP 200 plus marker; port released) |
| E2E-08 | Navigator migrate | dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- migrate | OPENCONNECT_NAVIGATOR_SQLITE_PATH = temp file | exit 0, Schema ensured | PASS (exit 0) |
| E2E-09 | Navigator lease-demo plus rerun | same project -- lease-demo twice | same temp file | exit 0 both times (rerun-safe assertions) | PASS (exit 0 both runs) |
| E2E-10 | Navigator outbox-relay-demo plus rerun | same project -- outbox-relay-demo twice | same temp file | exit 0 both times (rerun-safe assertions) | PASS (exit 0 both runs) |
| E2E-11 | Navigator verify | same project -- verify | same temp file | exit 0, Verify OK, none pending | PASS (exit 0) |
| E2E-12 | PostgreSql parity (offline) | dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- parity | none (no connection, no seeding) | exit 0, legacy 8-table parity reported | PASS (exit 0; offline parity) |
| E2E-13 | PostgreSql run plus verify (LIVE) | same project -- run, -- verify | GATE: requires BOTH OPENCONNECT_E2E_POSTGRES_HOST and OPENCONNECT_POSTGRESQL_CONNECTION already set; harness never derives credentials | exit 0 against disposable PG, else NOT VERIFIED with exact gate | NOT VERIFIED (missing-fixture gate; see execution record) |
| E2E-14 | SqlServer (SKIP) | none; intentionally not executed | none | NOT VERIFIED: no SQL Server fixture exists and none is being provisioned in this task | NOT VERIFIED (SKIP; no fixture, none provisioned) |



## Execution record (final proving run)

- Date (UTC): 2026-11-09; HEAD at execution: ad65ad8e354e4dc431cec683c2ca6bd540f83884 (re-verified read-only by the runner on every run).
- OPENCONNECT_E2E_POSTGRES_HOST at execution: unset (and OPENCONNECT_POSTGRESQL_CONNECTION also unset); no live PG attempted.
- Orchestrator command: powershell -NoProfile -ExecutionPolicy Bypass -File samples/EndToEnd/Run-E2E.ps1 -StepTimeoutSec 300 -CaseTimeoutSec 1200 -BlazorPort 5399 -BlazorStartupTimeoutSec 300
- Per-case exit codes: Minimal=0 Sqlite=0 InMemory=0 Blazor=0 Navigator=0 PostgreSql=3 SqlServer=3.
- Orchestrator exit: 0 (no FAIL cell; NOT VERIFIED cells do not fail the run).
- Cleanup: tempRoot=removed (temp DBs plus logs removed; per-case E2E-CLEANUP lines all reported removed or not-applicable).

## Anomalies found while proving this harness (both fixed in owned scripts)

- A1 (harness bug, fixed): Run-Blazor.ps1 initially lacked the TimeoutSec
  parameter the orchestrator passes to every case, so the Blazor cell failed
  with NamedParameterNotFound under the orchestrator while passing standalone.
  Fixed by adding the parameter; confirmed PASS via orchestrator-style
  invocation and the final full-matrix run. No product code involved.
- A2 (harness robustness, fixed): dotnet run parents a dotnet exec child, so
  killing only the parent can orphan the app server and hold the loopback
  port. Run-Blazor.ps1 now pre-flights the port (fail-fast
  port-in-use-at-start) and stops the whole process tree, verifying
  portReleased=True (observed True in the final run).

## Residual risks / NOT VERIFIED follow-ups

1. Cross-process fencing beyond the deterministic single-process Navigator
   demo is not covered by this harness (demos derive clocks arithmetically;
   no real concurrency harness).
2. PostgreSql live matrix (E2E-13) needs a disposable-PG fixture decision
   (OPENCONNECT_E2E_POSTGRES_HOST plus a provided
   OPENCONNECT_POSTGRESQL_CONNECTION); Testcontainers-based PG matrix is out
   of scope for this task (no image pulls).
3. SqlServer cell (E2E-14) needs a fixture/provisioning decision outside this
   task.
4. Receipt-carrier paths remain UNDECIDED: no harness coverage by contract.
5. Full-repo suite scope (all test projects, container-gated suites) is owned
   by the wider verification lane, not this sample harness.
