# ADR-002: Durable/A2A Deferral

**Status:** Accepted

**Date:** 2026-11-09

**Deciders:** Master orchestrator (deferral decision record; NOT implementation)

**Supersedes:** Nothing. Supplements ADR-001 (fail-closed gates remain authoritative).

## Context

ADR-001 records the agent-execution profile support on MAF 1.27.0: Direct execution and host-constructed `Workflow` are supported; Harness, DurableWorkflow, MCP, and A2A execution profiles fail closed via `MafCapabilityUnavailableException`.

Since ADR-001, two packages became referenceable on the repo's target framework:

- `Microsoft.Agents.AI.Harness@1.27.0` referenced (commit `e11b8c6`); `HarnessAgent` probe Available; capability matrix 12/12 including Harness; suite 491 passed / 0 failed / 1 skipped of 492 on `fc19d79`.
- `Microsoft.Agents.AI.Declarative.Mcp@1.27.0` referenced (commit `fc19d79`); `DefaultMcpToolHandler` probe Available; matrix 12/12.

Durable and A2A remain in a different position. NuGet metadata observed in AG-PLAN-001 shows:

- Durable hosting (`Hosting.AzureFunctions`) is preview-only and net10-only, against a repo target of net8.0.
- A2A hosting and SDK packages are preview-only.

No stable, net8-compatible Durable/A2A package is available to reference. Per-criterion behaviors for Harness (plan/execute, multi-turn, compaction, skills, file-access, approval, delegation), live MCP round-trip, and A2A/AG-UI classification remain NOT RUN: the Harness-Available probe and MCP adapter-assignability do not satisfy those criteria, and no per-criterion commands were executed by this slice.

## Decision

- **DEFER** Durable workflow and A2A integration implementation.
- Gates for DurableWorkflow and A2A profiles stay fail-closed per ADR-001 (`MafCapabilityUnavailableException`; nothing implemented, emulated, or re-built in-repo).
- **No prerelease exception and no TFM exception:** the repo does not take a preview-only dependency and does not move off net8.0 to accommodate a preview-only, net10-only Durable host.
- Checklist items OC2-D-MAF-003 through OC2-D-MAF-010 (Harness behaviors), OC2-D-MAF-017 (Durable), and OC2-D-MAF-019 (MCP/A2A) remain unchecked (`- [ ]`) and are marked BLOCKED with package facts, commit, and resumption — never flipped to PASS by this slice.

## Alternatives Considered

1. **Reference the preview Durable/A2A packages now — Rejected.** Taking preview-only dependencies, and a net10-only host against a net8.0 repo, would violate release and compatibility policy for no executable gain.
2. **Rebuild durable execution in-repo — Rejected.** Same no-rebuild rule as ADR-001; the router gates instead.
3. **Mark Durable/A2A NOT RUN indefinitely without a resumption condition — Rejected.** A bounded resumption condition keeps the deferral honest and reviewable.

## Consequences

- DurableWorkflow and A2A requests continue to fail closed with `MafCapabilityUnavailableException` and a user-safe message; no fake durable execution.
- OC2-D-MAF-003–010 stay BLOCKED-with-package-available: the Harness package reference and Available probe are recorded as evidence, but plan/execute, multi-turn, compaction, skills, file-access, approval, and background-delegation runs are still NOT RUN.
- OC2-D-MAF-019 stays BLOCKED-with-package-available: the Declarative.Mcp reference and `DefaultMcpToolHandler` Available probe are recorded, but live MCP round-trip plus A2A/AG-UI classification are still NOT RUN.
- OC2-D-MAF-017 stays BLOCKED (deferred per this ADR).
- No PASS flips occur in this slice.

## Evidence

- Baseline commits: `e11b8c6` (Harness package + probe), `fc19d79` (Declarative.Mcp package + probe; current HEAD at time of writing).
- Suite on `fc19d79`: 491 passed / 0 failed / 1 skipped of 492 (supplied per task packet; commands not re-executed by this documentation slice).
- Matrix: 12/12 including Harness (supplied per task packet).
- NuGet metadata observation: AG-PLAN-001 (Durable hosting preview-only + net10-only; A2A hosting + SDK preview-only).
- Fail-closed authority: ADR-001 (`docs/architecture/adr/ADR-001-agent-execution-profiles.md`).

## Resumption Condition

Re-evaluate this ADR when stable, net8-compatible Durable and A2A packages ship. Then, in order: reference the stable packages, flip the reflection probe honestly, and run the deferred labs (durable-store restart/lease lab; live MCP round-trip + A2A/AG-UI classification lab; remaining Harness behavior labs per checklist resumption notes). Do not claim Durable/A2A works before that evidence exists.
