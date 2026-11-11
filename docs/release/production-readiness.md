# Production readiness assessment

**Assessment date:** 2026-11-11
**Repository revision:** `c179954469d58e7f0d55714bd0e883c883fa6d3f` on `master`
**Scope:** canonical checkout only. Existing worktrees are excluded.

## Executive result

**PARTIAL.** OpenConnect has a substantial .NET-first foundation, pinned Microsoft Agent Framework 1.27.0 laboratory coverage, privacy/security contracts, EF Core infrastructure, worker images, and release workflows. It is not production-ready as a whole because current verification is not green, durable MAF/hosting support is blocked on stable packages, live worker and registry gates are not executed, and the default HTTP gateway still uses process-local task/session stores.

## Evidence status by subsystem

| Subsystem | Status | Current evidence and limitation |
|---|---|---|
| Repository governance and source history | VERIFIED | Clean `master` at `c179954`; first commit is 2026-11-06, inside Kiroween's 2026-10-30 to 2026-12-04 submission period. Historical checklist evidence is retained but not promoted automatically. |
| Locked .NET restore | VERIFIED | `dotnet restore OpenConnect.slnx --locked-mode` passed locally. |
| MAF Direct/Harness/Workflow laboratory | PARTIAL | Checklist records native MAF 1.27.0 lab evidence, but the current full solution test run has two failures. Durable Extension is BLOCKED by the repository's stable net8 compatibility gate. |
| Public composition and registration | PARTIAL | `AddOpenConnect`, typed agent registration, validation, capability registration, and fail-closed defaults exist. Some APIs remain partial and the older Kiro design names assemblies that are not in the solution. |
| ASP.NET Core gateway | PARTIAL | Authenticated task/session/capability routes and MAF task execution exist. `MapOpenConnect` resolves `InMemoryTaskStore` and `InMemorySessionStore` by default; this is not durable production admission. |
| Tenant authorization | PARTIAL | Route-level tenant/principal checks and sample negative tests exist. Production authentication and complete effect-boundary reauthorization evidence are not verified in this audit. |
| Privacy firewall and vault | PARTIAL | .NET privacy code, rules detector, tokenization/vault and fail-closed requirements exist. Full external model/tool/worker boundary evidence is incomplete. |
| Security worker rules-only | PARTIAL | Worker code and sample fixtures exist. T0/T1 execution is currently BLOCKED by CRLF shell scripts on the Linux shell path; container T2/T3 was not completed in this audit. |
| EF Core persistence | PARTIAL | `OpenConnect.Persistence.EFCore` and tests exist. Gateway wiring to replace in-memory stores and crash/outbox recovery remain unverified. The old `src/OpenConnect.Persistence.Postgres` directory exists outside the solution and conflicts with newer provider-neutral policy. |
| NATS transport/outbox | PARTIAL | NATS publisher and infra Compose exist. Live broker redelivery, ACL, payload-reference and recovery proof are not verified. |
| Clients, Blazor, MiniComposer | PARTIAL | Client/component projects and tests exist. A real ASP.NET Core + Blazor MAF demonstration and browser journey evidence remain incomplete. |
| Voice/Navigator | DEFERRED | Optional packages/workers and contracts exist; model/browser/live evidence is not a release claim. |
| Telemetry and cost metering | PARTIAL | Observability and metering packages/tests exist. Production exporter failure, model-cost reconciliation and tenant-safe operational evidence are incomplete. |
| Worker fleet | PARTIAL | 11 worker/sample paths and CI matrix exist. Model-live, offline revision, release and registry evidence are intentionally manual or blocked. |
| Cross-framework consumers | NOT VERIFIED | CI defines net8/net9/net10 package inspection and consumer smoke, but no successful current local or CI artifact was supplied for this revision. |
| NuGet release | PARTIAL | NuGet currently exposes OpenConnect packages through version `1.2.1` (checked via the public flat-container API). Current source pack/consumer compatibility and release provenance are not verified here. Publishing remains owner-only and gated by protected environment approval. |
| Docker Hub release | BLOCKED | Source/configuration uses the corrected namespace `vuminkosimatibe`. No registry publish, pull-by-digest, SBOM/signature or 11-image release proof was run. |
| Reproducible deployment | PARTIAL | `infra/compose.yml` parses with Docker Compose. The checked local env expands credential-shaped values, so it must not be treated as release evidence and any real credentials must be rotated. Image digests are not consistently pinned in all manifests. |

## Exact current failures and limitations

- The full non-live test run observed **1433 total, 1430 passed, 2 failed, 1 skipped**. Failures: `NativeWorkflowInteractionTests.Dispose_stops_a_run_waiting_for_native_external_request` and `McpLiveRoundTripLabTests.InvokeToolAsync_unreachable_server_throws_without_hanging`. The first failure is a disposal/cancellation hang; the second is an unreachable MCP timeout that exceeded the test bound.
- The concurrent full build attempt failed with file-copy locks from parallel test hosts (`MSB3026`), not a source compiler error. Run build and tests as separate, serialized operations after stale testhost processes exit.
- `bash ./samples/workers/shared/scripts/run-one.sh ...` fails before executing the sample because the checked-in shell scripts have CRLF line endings. The failure includes `set: -\r: invalid option` and a path containing `lib.sh\r`.
- No current evidence proves a real model provider, durable MAF backend, production authentication provider, published Docker image, signed SBOM, registry pull, or live NATS recovery.
- `src/OpenConnect.Persistence.Postgres` and historical references in `AGENTS.md`, `README.md`, and `.kiro/steering/architecture.md` conflict with the current EF-provider-neutral direction. They require reconciliation before release claims.

## Owner-only actions

1. Configure and approve the GitHub `production` environment reviewers and NuGet trusted publishing policy. Do not place tokens in the repository.
2. Approve Docker Hub credentials and publish authorization for `vuminkosimatibe`; run the 11-image release only from a protected release workflow.
3. Supply licensed, immutable model manifests and any required GPU/registry access for T4/T5 worker tests.
4. Decide whether to remove, archive, or explicitly quarantine the legacy `OpenConnect.Persistence.Postgres` source after migration review.
5. Provide a production authentication/tenant identity integration and a disposable relational/NATS environment for recovery tests.

## Verification ledger

| Check | Result | Command/tool | Scope |
|---|---|---|---|
| Git baseline | PASS | `git status --short`, `git branch --show-current`, `git rev-parse HEAD` | clean `master`, current revision above |
| Locked restore | PASS | `dotnet restore OpenConnect.slnx --locked-mode` | full solution dependency graph |
| Docker availability | PASS | `docker version` | Docker Desktop Linux engine |
| Infra Compose parse | PASS | `docker compose -f infra/compose.yml config` | expanded local config; values redacted in this document |
| Security Compose parse | PASS | `docker compose -f samples/workers/security/compose.e2e.yaml config` | security sample |
| Non-live .NET tests | FAIL | `dotnet test OpenConnect.slnx -c Release --no-build --filter "Category!=NatsLive"` | 2 failures, 1 skip |
| Full Release build | FAIL/UNSTABLE | `dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror` | parallel file locks observed |
| Security T0/T1 scripts | FAIL | `bash ./samples/workers/shared/scripts/run-one.sh security --tier static/contract --provider sqlite` | blocked by CRLF shell scripts |
| Published package lookup | PASS | NuGet flat-container API for OpenConnect packages | public versions through 1.2.1; not source compatibility proof |
| Live worker, registry, production migration | NOT VERIFIED | not run | intentionally requires approved isolated environments |
