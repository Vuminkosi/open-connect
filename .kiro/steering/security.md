---
inclusion: auto
name: openconnect-security
description: Authentication, authorization, privacy firewall, PII handling, and secure execution for OpenConnect
---

# OpenConnect Security Steering

## Core Security Principles

1. **Security and privacy by default** - Firewall enabled, auth always active
2. **Fail closed** - Missing detector blocks egress, not silently bypasses
3. **Host owns policy** - Workers are untrusted for authorization
4. **Pseudonymization is not anonymization** - Masked content may still reveal information
5. **No borrowed benchmark claims** - Only report measured, reproducible results

## Authentication and Authorization

### Always-On Security

- Authentication and authorization remain active even when `Privacy.Enabled = false`
- `Privacy.Enabled = false` requires explicit trusted server configuration
- Emits security warning/metric on startup when privacy is disabled
- Never disables authN, authZ, tenant isolation, tool approvals, or audit

### Principal Isolation

- Security bindings follow authenticated host principal, not client-supplied `tenantId`
- Server derives scoped immutable auth snapshot at task admission
- Reauthorization required before every sensitive tool call
- Browser disconnect/reconnect does not grant new rights

### Interactive vs Operational Identity

| Context | Interactive | Operational |
|---------|-------------|-------------|
| Caller identity | Authenticated current user | Host-managed workload identity |
| Auth information | Derived claims/scopes | Scoped workload grants |
| Activation | HTTP/WS/user action | Scheduled, event-triggered |
| Hosting | Request processor | BackgroundService monitor |
| Tool authorization | Active principal + task | Workload principal + run ID |
| UI actions | Authorized proposals | No direct UI activation |

## Privacy Firewall Architecture

### Deployment Model

```
Client / SDK / Tool
       |
ASP.NET Core Host
+ Privacy policy evaluator
+ Egress interlock
+ Local encrypted token vault
       |
Authenticated LOCAL security-worker channel
       |
Python Security Worker (Docker, opt-in)
- Rules, checksums, secrets detection
- Pinned on-device NER (optional)
- No vault keys, no authz authority
       |
.NET policy engine -> tokenize -> encrypt mappings
       |
Outbound egress gate -> approved endpoint
       |
Scoped response restoration
```

### Host vs Worker Responsibilities

| Host (.NET) | Worker (Python) |
|-------------|-----------------|
| Policy decisions | Detection signals |
| Token issuance | Span proposals |
| Token vault (encrypted) | No vault access |
| Authorization checks | No authz authority |
| Egress gates | Classification only |
| Restoration control | No restoration capability |

### Deployment Modes

| Mode | Detector Location | External Egress | Missing Detector |
|------|-------------------|-----------------|------------------|
| `strict-local` | Local only | Sanitized only | Block |
| `local-only` | Local detection | No remote | Fail or remain local |
| `policy-controlled` | Policy-defined | Policy-defined | Deny or approved local |
| `disabled` | None | Normal flow | N/A (explicit opt-out) |

## Detection Pipeline

### Entity Categories (Minimum)

- `PERSON`, `EMAIL`, `PHONE`, `ADDRESS`
- `DATE_OF_BIRTH`, `GOVERNMENT_ID`, `ACCOUNT_NUMBER`
- `PAYMENT_CARD`, `IP_ADDRESS`, `SECRET`
- `ORG_SENSITIVE`, `CUSTOM`

### Detector Types

| Detector | Default | Purpose |
|----------|---------|---------|
| Rules | **ON** | Patterns, checksums, secrets |
| GLiNER PII | OFF | Semantic entity detection |
| DeBERTa injection | OFF | Injection risk detection |
| Qwen3Guard | OFF | Safety classification |
| PaddleOCR | OFF | Image text extraction |
| YuNet | OFF | Face detection |

**Rule:** Disabled detectors MUST NOT download, load, initialize, or advertise capabilities.

### Processing Pipeline

1. Validate content and size
2. Canonicalize without semantic change
3. Extract text/structured fields
4. Deterministic validators (email, phone, IDs, secrets)
5. Optional enabled local NER
6. Overlap/type resolution
7. Confidence/risk classification
8. Policy decision
9. Placeholder mapping
10. Schema reconstruction
11. Second-pass egress validation

## Token Vault and Restoration

### Placeholder Design

- Cryptographically random, unpredictable tokens
- No raw value, tenant ID, or discernible identifier embedded
- Format: `[[OC_<TYPE>_<RANDOM>]]` (illustrative)
- Collision-resistant identifiers

### Vault Requirements

- Encrypted storage (AES-256-GCM minimum)
- Per-record random nonce
- Key material from KMS/secret provider
- Binding to tenant + principal + session + turn
- TTL and key rotation support
- No cross-scope token resolution

### Restoration Rules

- Explicit operation requiring authorized destination
- Exact field-path allowlist
- Schema-aware for tool invocation
- Only for trusted, authorized tools
- Never in system prompts, audit records, or untrusted payloads
- Streaming-safe with split-token handling

## Egress Boundaries

### Trust Boundary Matrix

| Boundary | Scan Before Egress? | Plaintext Recipient | Restoration |
|----------|---------------------|---------------------|-------------|
| Host -> External LLM | **Always** | None (masked) | Never |
| Host -> Local worker | Policy decision | Permitted local | Never |
| Host -> External tool | Schema-aware scan | Masked only | Deny |
| Host -> Trusted local tool | Schema-aware scan | Approved fields only | Exact fields |
| Tool response -> model | **Always** | Sanitized | Never |
| Host -> NATS | Sanitize | Metadata/ref only | Never |
| Response -> user | Output scan | Authorized owner | Policy-controlled |
| Telemetry/logs | Sanitized events | Safe metadata | Never |

### Fail-Closed Behavior

- Missing worker blocks strict-mode egress
- Unready detector blocks flows requiring its coverage
- No silent fallback to rules-only when semantic PII required
- Policy-required detection failures return `privacy_blocked` or equivalent

## Tool Security

### Discovery and Authorization

- Agent-scoped discovery filtered by: agent allowlist ∩ principal scopes ∩ tenant policy ∩ task context
- Schema validation before execution
- Reauthorization at execution time
- Approvals bound to: exact normalized args hash, principal, tenant, session, expiry

### Prohibited Actions

- Untrusted instructions cannot modify tool permissions
- Tool results cannot change agent capabilities
- Model output cannot grant authority
- Prompt injection cannot escalate privileges

### Approval Requirements

- Side-effect tools require explicit approval
- Approval receipts scoped to exact arguments
- Expired approvals rejected before execution
- Invalid schemas rejected before execution

## Multi-Tenancy

### Isolation Requirements

- All ownership lookups enforce tenant/principal
- No direct worker DB permission
- Cross-tenant token replay denied
- Tenant policy can tighten (never loosen) global policy

### Data Handling

- Raw content never in NATS notices
- Point-to-point authenticated transport for scanning
- External providers see only sanitized payloads
- Tenant-scoped token mappings

## Secrets and Credentials

### Secrets Management

- Secrets resolved from host secret stores
- Never from `agent-runtime.md`, YAML prompts, or checked-in config
- mTLS for host-to-worker communication
- Worker credentials scoped to worker identity only

### Secrets Detection Priority

- Passwords, API keys, access tokens have highest severity
- Secrets NEVER restored to model output, tool arguments, logs, or telemetry
- Final response scanner catches independently inferred values

## Testing Requirements

### Adversarial Test Matrix

| Category | Required Outcome |
|----------|------------------|
| Entity detection | Deterministic spans, measured miss/overmask |
| Privacy enforcement | No unauthorized egress, stable failure code |
| Vault | Zero cross-tenant restoration |
| Tool | Zero unauthorized execution/unmask |
| Streaming | No partial leakage, deterministic restoration |
| Messaging | Zero raw canaries in envelopes |
| Worker | Reject forged/stale identity |
| Media | Deny unsupported/unsafe egress |
| Configuration | Deny policy modification via agent text |
| Disabled mode | Zero security bypass |

### Acceptance Gates (Stable Release)

- AT-021: Security worker identity/readiness/fail-closed
- AT-022: Local rules + opt-in NER evaluation (when enabled)
- AT-023: Vault and scoped restoration
- AT-024: Egress no-leak enforcement
- AT-025: Streaming restoration safety
- AT-028: Security-worker container hardening
- AT-029: Firewall CI and regression tests
- AT-030: Security worker published image verification

## Operational Security

### Docker Security

- Non-root execution
- Pinned base digests
- Locked dependencies
- No model weights in published images (unless licensed)
- No secrets in image layers or logs
- Network egress restrictions for detector container

### Release Security

- Signed images with SBOM
- Vulnerability scanning
- Dependency license audit
- Provenance attestation
- No secrets in release artifacts

## Threat Model Summary

Key threat categories addressed:
- Cross-tenant data access
- Prompt injection
- SSRF
- Token replay/collision
- Model data leakage
- Worker compromise
- Configuration tampering
- Unauthorized tool execution
- Partial streaming leakage
