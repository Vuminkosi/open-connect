# ADR-001: Agent Execution Profiles

**Status:** Accepted

**Date:** 2026-11-09

**Deciders:** Master orchestrator (per agents-spec §15, AG2-DUR-002 ADR requirement, §0 Rule 2)

## Context

On MAF 1.27.0, reflection evidence establishes the available agent-execution surface:

- Present: `WorkflowBuilder` + `Workflow`.
- Absent: `HarnessAgent`, `DurableWorkflowExecutor`, `McpClient`, `A2AClient`.
- 299 types enumerated in the probe.

The router must therefore gate on what MAF 1.27.0 actually exposes, and fail closed (via `MafCapabilityUnavailableException`) for anything it does not.

## Decision

- **Supported:** Direct execution and host-constructed `Workflow`.
- **Explicitly Unsupported:** Harness, DurableWorkflow, MCP, A2A execution profiles.
- Unsupported profiles fail closed with `MafCapabilityUnavailableException`; they are not implemented, emulated, or re-built in-repo.

## Alternatives Considered

1. **Implement a custom harness in-repo — Rejected.** Rebuilding framework capability inside the repo violates the no-rebuild rule; the router gates instead.
2. **Upgrade MAF to gain the missing types — Deferred.** Pending package availability of a MAF version that exposes the absent types.

## Consequences

- The router gates execution profiles on the reflection probe; unsupported profiles are rejected fail-closed.
- Test suite green on baseline commit `7ff2d54`: 490 passed / 1 skipped / 0 failed.
- Atlas (samples/ArchitectureAtlas/) is excluded from this decision's scope.

## Evidence

- Baseline commit: `7ff2d54` (master HEAD, observed 2026-11-09).
- Suite result on `7ff2d54`: 490 passed / 1 skipped / 0 failed (supplied evidence; commands not re-executed by this documentation slice).
- Probe type names: `WorkflowBuilder`, `Workflow` (present); `HarnessAgent`, `DurableWorkflowExecutor`, `McpClient`, `A2AClient` (absent); 299 types enumerated.

## Resumption Condition

Re-evaluate this ADR when the referenced MAF package exposes the currently absent types. The reflection probe flips honestly with zero code change at that point; no router rewrite is required to detect newly available capability.
