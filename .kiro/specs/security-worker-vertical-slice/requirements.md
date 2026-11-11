# Security worker vertical slice requirements

**Status:** Planned, evidence-gated.  
**Scope:** rules-only security worker with a private authenticated scan contract. No model download, registry publish, production migration, or production credentials.

## Requirements

- **SEC-SLICE-REQ-001:** The rules-only worker starts with `rules` enabled and all model-backed detectors disabled.
- **SEC-SLICE-REQ-002:** The worker exposes live and readiness health separately; readiness advertises only installed/enabled capabilities.
- **SEC-SLICE-REQ-003:** Authenticated scan requests return the versioned response contract with Unicode-safe spans.
- **SEC-SLICE-REQ-004:** Missing or disabled detector coverage returns a typed unavailable response; it never silently downgrades a protected operation.
- **SEC-SLICE-REQ-005:** Unauthenticated scan requests are rejected with HTTP 401.
- **SEC-SLICE-REQ-006:** The sample harness must execute on the supported Linux shell path without CRLF parsing failures.
- **SEC-SLICE-REQ-007:** Test evidence must be sanitized and must not include secrets or raw sensitive fixture content beyond approved synthetic fixtures.

## Out of scope

Model-live/revision tests, production authentication, Docker Hub publishing, external sink deployment, durable gateway persistence, and replacing MAF execution.

## Acceptance

T0 Compose parse; T1 fixture/cache contract; T2 container readiness; T3 authenticated scan plus 401 and unavailable-detector negatives. Record command, revision, image digest, result and artifact path in evidence.
