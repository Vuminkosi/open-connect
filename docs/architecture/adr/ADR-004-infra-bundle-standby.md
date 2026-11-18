# ADR-004: Infra-Bundle Standby Decisions (S08)

**Status:** Accepted

**Date:** 2026-11-10

**Deciders:** Feature lead (S08-DESIGN dispositions) via master orchestrator

## Context

S08 probed two candidate infrastructure bundles — a NATS connection bundle and an observability wiring bundle — plus live-broker CI posture and Infisical integration. All four legs were dispositioned to standby: the existing hand-wiring is the contract, and no bundle was merged.

Probe findings (source-verified at HEAD `e5d2c5c`):

- **NATS hand-wiring is the live contract.** `workers/outbox/Program.cs:36-39` constructs `NatsJetStreamPublisher` directly from the `NatsUrl` option, then registers `IJetStreamPublisher` and `NatsPublisher` from that instance. No bundle, factory, or DI extension sits in between. A NATS bundle would add indirection over this without a second consumer to justify it.
- **Observability `new` + `using` is the live contract.** `src/OpenConnect.Observability/README.md:15-26` documents the usage as `using var listener = new SafeActivityListener();` with `SafeActivityListener` defined at `src/OpenConnect.Observability/SafeActivityListener.cs:6-10`. No bundle or hosting extension is referenced. A bundle would wrap a one-line construction the consumer already owns.
- **Live NATS never gates CI.** Commit `413147c` ("ci: exclude live NATS tests from required legs") changed `.github/workflows/ci.yml` and `.github/workflows/cross-framework-integration.yml` so required legs run with `Category!=NatsLive`; `Category=NatsLive` tests are opt-in only.
- **Infisical is absent by standing posture.** The only in-repo references are `samples/ArchitectureAtlas/Program.cs:125` and `samples/ArchitectureAtlas/README.md:210`, both marking NATS JetStream / Qdrant / Infisical KMS / OTLP as explicitly not required (`ABSENT_SAFE`). No Infisical integration exists to bundle; deferral stands.

Constraints: MAF-first; additive-only; optional infrastructure is never mandatory.

## Decision

Four standing rules. Each carries its reopen trigger — a bundle is reconsidered only when the trigger fires, never speculatively.

1. **NATS bundle REJECTED — hand-wiring is the contract.** `workers/outbox/Program.cs:36-39` is the authoritative wiring: read `NatsUrl`, construct `NatsJetStreamPublisher`, register `IJetStreamPublisher` and `NatsPublisher`. Do not introduce a NATS connection bundle, options-binder, or DI extension that duplicates this path.
   - **Reopen trigger:** a second host needs the same relay wiring (i.e., genuine shared construction logic across two hosts), or the connection surface gains non-default options the hand-wiring cannot express cleanly.

2. **Observability bundle REJECTED — `new` + `using` is the contract.** `src/OpenConnect.Observability/README.md:15-26` plus `SafeActivityListener.cs:6-10` define the usage: construct with an optional `ITelemetryPrivacyFilter`, dispose via `using`. Do not introduce an observability bundle or hosting extension over this.
   - **Reopen trigger:** telemetry gains non-default options (exporters, sampling, multi-listener composition) that one-line construction cannot express, with at least one consumer requiring them.

3. **Live-NATS CI exclusion + opt-in stands.** Required legs exclude `Category=NatsLive` (commit `413147c`: `ci.yml` legs run `dotnet test ... --filter "Category!=NatsLive"`; `cross-framework-integration.yml` runs `--filter "FullyQualifiedName~Nats&Category!=NatsLive"`). Fake-broker tests gate; live-broker tests never gate. Opt-in (documented in the workflow comments; unexecuted by this slice):
   - Disposable broker: `docker run --rm -p 4222:4222 nats -js`
   - `$env:OPENCONNECT_TEST_NATS_URL='nats://localhost:4222'; dotnet test --filter "Category=NatsLive"`
   - **Reopen trigger:** a concrete live-broker regression escapes fake-broker coverage and is reproducible only against a real broker — then scope the minimal live leg, do not re-gate the suite by default.

4. **Infisical deferral stands.** No Infisical integration exists; the Atlas posture (`samples/ArchitectureAtlas/Program.cs:125`, `README.md:210`) records external KMS as explicitly not required. No secret-fetching, KMS client, or config-provider work starts on speculation.
   - **Reopen trigger:** an approved requirement names a concrete secret-management gap (rotation, envelope encryption, multi-environment parity) that file/env configuration cannot satisfy.

## Alternatives Considered

1. **Merge one or both bundles now — Rejected.** Adds API surface and maintenance weight with a single consumer per path and no non-default options to manage. Violates additive-only restraint; rejected per lead disposition.
2. **Re-gate CI on live NATS — Rejected.** Live brokers are non-hermetic and unavailable on hosted runners; fake-broker tests already gate the contract. Live coverage stays opt-in.
3. **Start Infisical integration speculatively — Deferred.** No approved requirement or consumer exists; deferral stands until the reopen trigger fires.

## Consequences

- No `PublicAPI.Shipped.txt` change: nothing was added to or removed from any public surface.
- Fake-broker tests remain the gate for transport behavior; live tests are opt-in via `OPENCONNECT_TEST_NATS_URL` plus a disposable broker (commands above; unexecuted by this documentation slice).
- BLOCKED S08 bundle legs close honestly as REJECTED/deferred-by-rule rather than as pending implementation.
- Any reopen trigger firing requires a new ADR or ADR amendment before bundle work begins; the trigger, not enthusiasm, authorizes the work.

## Evidence

- Baseline HEAD: `e5d2c5c` (observed via `git rev-parse --short HEAD`, 2026-11-10).
- `workers/outbox/Program.cs:36-39` — NATS hand-wiring (inspected; note: assignment brief cited lines ~37-41, actual construction block is 36-39).
- `src/OpenConnect.Observability/README.md:15-26` and `SafeActivityListener.cs:6-10` — `new` + `using` contract (inspected).
- Commit `413147c` (`git show --stat` + full diff of both workflow files; inspected): `.github/workflows/ci.yml` (+12/−3 across commit), `.github/workflows/cross-framework-integration.yml` (+6/−3 across commit); opt-in command quoted verbatim from committed workflow comments.
- `samples/ArchitectureAtlas/Program.cs:125`, `samples/ArchitectureAtlas/README.md:210` — Infisical/KMS explicitly not required (inspected via grep).
- Opt-in broker commands: quoted from committed workflow comments, NOT VERIFIED (not executed by this slice).

## Resumption Condition

Re-evaluate this ADR only when a stated reopen trigger fires: second host needing relay wiring, non-default telemetry options with a real consumer, a live-only broker regression, or an approved secret-management requirement. The trigger and its evidence must be recorded before any bundle work starts.
