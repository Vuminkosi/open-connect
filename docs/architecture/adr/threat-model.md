# OpenConnect Threat Model

**Status:** Proposed security-test guide; contract-derived, implementation and runtime verification not performed.
**Audience:** Security engineers and test engineers.
**Scope:** System trust boundaries and adversarial scenarios for OpenConnect's host, workers, transport, persistence, tools, clients, and external providers.

This document translates approved security invariants into threat scenarios and test targets. It is not evidence that controls are implemented or tests pass. “Mitigations” below are approved requirements or principles; verify implementation before treating them as effective.

## System boundaries

| Boundary | Trust relationship and protected assets | Approved control / requirement | Test traceability |
| --- | --- | --- | --- |
| Client/SDK ↔ .NET host (gateway) | Client input is untrusted; protect principal, tenant scope, sessions, and submitted content. | Host derives identity and policy from authenticated context; clients cannot grant rights. | AT-016 (Gateway); exact scenario coverage must be confirmed. |
| .NET host ↔ agent/model execution | Model and agent instructions are untrusted; protect authorization, policy, and private content. | Host owns policy; all model egress passes an enforceable gate. | AT-003, AT-004 (agent runtime); AT-024 (egress no-leak). |
| .NET host ↔ Python security worker | Worker supplies detection signals only and is not an authorization authority. | Authenticated point-to-point channel; worker has no vault keys, database credentials, or authorization capability; fail closed when required detection is unavailable. | AT-021 (identity/readiness/fail-closed); AT-028 (container hardening). |
| Host ↔ NATS JetStream | Broker/consumers are separate trust boundary; protect message contents and tenant/session metadata. | Raw content never enters NATS; sanitize messages and use authenticated transport. | AT-029 (firewall CI/regression); AT-039/AT-040 (privacy firewall acceptance family). Exact individual case mapping is not specified in source. |
| Host ↔ PostgreSQL/token vault | Persistent state and token mappings are sensitive; enforce tenant/principal/session/turn isolation. | Encrypted vault, scope-bound mappings, no worker database access, deny cross-scope replay. | AT-023 (vault/scoped restoration); AT-004 where authorization applies. |
| Host ↔ external model/provider | External recipient is outside the trusted host boundary; protect raw PII and secrets. | Scan and sanitize before egress; missing required detection blocks strict-mode flow. | AT-024 (egress no-leak); AT-022 for enabled detector evaluation. |
| Host ↔ tools, retrieval, and agent handoffs | Tools and returned content can be malicious or compromised; protect permissions and secrets. | Schema validation, per-call reauthorization, scoped approvals, scan tool responses, exact-field restoration only for authorized destinations. | AT-004 (tool security); AT-023/AT-024 as applicable. |
| Host ↔ telemetry/logging | Observability systems and operators are a separate data boundary; protect raw content and credentials. | Emit sanitized events; never restore secrets into logs or telemetry. | AT-029 / AT-039 / AT-040 are relevant acceptance families; exact coverage is not defined by available sources. |

The architecture's approved trust statement is that the host owns policy and workers are untrusted. NATS is not browser-exposed per the task's approved invariant; its concrete deployment/network enforcement was not located and remains **UNVERIFIED**. No deployment topology beyond the referenced design is asserted here.

## Threat scenarios and security-test map

| ID | Lens | Boundary / attacker scenario | Required mitigation (contract-level) | Existing acceptance IDs to exercise |
| --- | --- | --- | --- | --- |
| TM-01 | STRIDE: spoofing, tampering; LINDDUN: linkability | A forged, stale, or wrong-scope worker identity submits a detection result or impersonates a worker. | Authenticate the point-to-point channel; reject forged/stale identity; worker cannot authorize. | AT-021; AT-028 |
| TM-02 | STRIDE: tampering, information disclosure; LINDDUN: disclosure | A compromised worker, model prompt, or malicious content attempts to bypass host privacy policy or obtain raw content. | Host makes policy decisions; worker only reports signals; gate all egress and fail closed for required missing/unready detection. | AT-021, AT-024; AT-022 when NER is enabled |
| TM-03 | STRIDE: information disclosure; LINDDUN: disclosure, identifiability | Raw content or secrets appear in NATS envelopes, external requests, logs, or telemetry. | Sanitize before egress; no raw content in NATS; secrets never restored to output/logs/telemetry. | AT-024, AT-029; AT-039/AT-040 where their defined cases cover the path |
| TM-04 | STRIDE: elevation of privilege, repudiation | Prompt injection or tampered tool output changes permissions, bypasses approval, or reuses an expired/altered approval. | Host authorization; per-call reauthorization; approval bound to normalized argument hash and identity scope; reject expired approvals and invalid schemas. | AT-004 |
| TM-05 | STRIDE: information disclosure, tampering; LINDDUN: linkability, disclosure | A token is replayed across tenants, principals, sessions, or turns, or a collision resolves to another value. | Random unpredictable tokens; encrypted scope-bound vault; deny cross-scope replay; TTL and key rotation. | AT-023 |
| TM-06 | STRIDE: information disclosure; LINDDUN: disclosure | Streaming/reconnect splits a placeholder or UTF-8 sequence and leaks a partial secret. | Buffer and restore safely across split tokens and reconnects; never emit partial secret material. | AT-025 |
| TM-07 | STRIDE: tampering, denial of service | Oversized, malformed, or ambiguous content exploits parsing or causes unsafe fallback. | Validate size/content; deterministic span resolution; required detector failure blocks or uses explicitly permitted verified local-only processing. | AT-022, AT-024, AT-029; concrete boundary cases require test-plan confirmation. |
| TM-08 | LINDDUN: non-repudiation, unawareness | Audit/telemetry omits security-relevant actions or captures more sensitive content than needed. | Record sanitized security events; do not log raw content or secrets. | No exact audit-specific AT-* mapping found; add/identify one before claiming coverage. |
| TM-09 | STRIDE: information disclosure, elevation of privilege; LINDDUN: disclosure | Tenant-controlled policy or agent text weakens global policy or disables security gates. | Tenant policy may tighten but not loosen global policy; agent text cannot modify policy; disabling privacy does not disable auth, authorization, tenant isolation, approvals, schema validation, or audit. | AT-004, AT-029; exact policy/configuration case mapping is not specified. |

LINDDUN is used as a privacy-threat lens alongside STRIDE; this is a focused boundary analysis, not a complete privacy impact assessment. Availability and accountability scenarios above are threat hypotheses derived from the stated boundary and required controls, not verified vulnerabilities.

## Test-ID and source caveats

- Existing identifiers are preserved as written in `.kiro/steering/security.md` and `.kiro/specs/openconnect-core/requirements.md`. The security steering document explicitly names AT-021–AT-025 and AT-028–AT-030; requirements additionally map AT-039 and AT-040 to the privacy firewall.
- The available sources do not define individual test procedures for each AT ID. Mappings indicate relevant acceptance targets, not proof that a particular scenario is covered or passing. Test engineers should bind each scenario to the authoritative test case before declaring coverage.
- No `OC-*` identifier occurrence was found in the inspected Markdown sources. Requirements document states that OC-* maps to source documents, but the referenced `docs/plans/spec.md` and `docs/plans/firewall.md` were not present in this checkout. No OC-* identifiers are invented here.
- `docs/checklist.md` is referenced by `.kiro/specs/openconnect-core/tasks.md` but was not present. The requested checklist item therefore could not be independently checked.
- The task references `firewall.md` §1 deployment; that source was not found. This document uses the available `.kiro/steering/security.md` deployment model and labels runtime/deployment enforcement unverified.
- The approved architecture reference `.kiro/specs/openconnect-core/design.md` is a draft implementation-ready design; it is contract/design context, not runtime evidence.

## Sources

- [OpenConnect security steering](../../../.kiro/steering/security.md) — principles, firewall deployment, egress boundaries, acceptance IDs.
- [OpenConnect requirements](../../../.kiro/specs/openconnect-core/requirements.md) — REQ-100–143, tool security requirements, acceptance-test traceability.
- [OpenConnect design specification](../../../.kiro/specs/openconnect-core/design.md) — draft layered architecture.
- [Repository engineering guide](../../../AGENTS.md) — project boundaries and security principles.
- [Implementation task index](../../../.kiro/specs/openconnect-core/tasks.md) — checklist/source references; does not establish their presence.

**Verification state:** Source documents inspected; implementation behavior, deployed controls, and test execution **NOT VERIFIED**. Relative-link and identifier checks should be run against this exact document before integration.
