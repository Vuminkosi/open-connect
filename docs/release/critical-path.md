# Critical path to production and submission

**Baseline:** `master` at `c179954` (2026-11-11).
**Working rule:** each slice follows Requirements → Design → Atomic Tasks → Implementation → Tests → Evidence → Review. A checklist checkbox changes only after executable evidence.

## Ordered work packages

| Priority | Work package | Exit evidence | Status |
|---:|---|---|---|
| 1 | Clean published consumers for net8/net9/net10 | Pack eligible packages, inspect assets/dependency groups, restore fresh consumers, run each against matching runtime | BLOCKED/NOT VERIFIED |
| 2 | Native MAF sample: ASP.NET Core + Blazor with Direct, Harness and Workflow profiles | Real pinned MAF 1.27.0 execution, streamed updates, tool call, native Harness state, native Workflow events, browser journey | PARTIAL |
| 3 | Security rules-only worker vertical slice | T0/T1/T2/T3 evidence, authenticated scan, Unicode spans, 401 negative, unavailable detector 503, zero external sink bytes | IMPLEMENTED; T0/T1 blocked by line endings |
| 4 | Tenant authorization, privacy and external-tool enforcement | Cross-tenant denial, exact approval binding, detector-unready fail-closed, SSRF/unsafe MCP denial | PARTIAL |
| 5 | EF relational persistence, native session boundary and transactional outbox recovery | Disposable PostgreSQL/SQLite tests, atomic admission, crash-before/after publish, redelivery dedup, tenant indexes | PARTIAL |
| 6 | Client event replay and MiniComposer | Bounded replay, reconnect cursor, accessible Blazor journey, no raw sensitive event data | PARTIAL |
| 7 | Optional voice/Navigator | Explicitly enabled worker/model/browser evidence and safe fallback | DEFERRED |
| 8 | Telemetry, cost metering, failure recovery | Redacted traces, exporter failure isolation, model usage/cost reconciliation, retry/lease proof | PARTIAL |
| 9 | Reproducible deployment and Kiro demo | Compose smoke, immutable image references, sanitized evidence bundle, public demo path | PARTIAL/BLOCKED |

## First executable vertical slice

The first slice is **security rules-only worker → authenticated private scan → host policy decision**, with no model download and no registry interaction. The current repository already contains the worker, fixtures, fail-closed detector behavior, Docker Compose, and test harness. The immediate implementation work is to make the shell harness executable on Linux by normalizing its line endings, then run T0/T1/T2/T3 in a disposable local Compose project. This slice does not claim the complete gateway privacy interlock until a real host-to-worker integration test records the deny path and external sink byte count.

## Dependency gates

- MAF Direct/Harness/Workflow proof precedes public execution-profile freeze.
- Tenant/effect authorization precedes external tool and worker release claims.
- EF transaction/outbox proof precedes durable task `202 Accepted` claims.
- Consumer smoke precedes NuGet release.
- Worker T4/T5 evidence and image provenance precede Docker Hub release.
- Kiro submission evidence must show the repository is new within the official period, contain public source/license and root `.kiro`, and include a working under-three-minute demo.

## Explicit non-goals for the first slice

No production migrations, registry publishing, live model provisioning, production credential use, replacement of MAF execution, or broad persistence refactor is included.
