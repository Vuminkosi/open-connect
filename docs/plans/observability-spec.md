# Observability specification

**Status:** Contract/design target; implementation status is explicitly called out per section. Audience: .NET maintainers integrating diagnostics, privacy enforcement, and usage metering.

## Scope and invariants

Observability is metadata-only by default. Raw prompts, completions, tool arguments/results, credentials, personal data, and unclassified fields are never eligible for export. Privacy enforcement remains in the .NET host. External telemetry egress is denied unless policy allows it and validation succeeds. Detector or policy failure is not a clean result. Metering is a distinct business record and remains available under its own controls.

## Current diagnostic sources and filter

`OpenConnect.Observability.Instrumentation` currently exposes activity sources `OpenConnect.Gateway`, `OpenConnect.Firewall`, `OpenConnect.GenAI`, `OpenConnect.Agents`, `OpenConnect.Workers`, `OpenConnect.Vault`, and `OpenConnect.Metering`, and meter `OpenConnect`. Its `MetadataOnly` constant is declarative only. `AllowlistedTags` accepts string values up to 64 characters for the exact keys `operation`, `provider`, `model_family`, `outcome`, `mode`, and `component`; it filters values containing email-like `@`, `bearer `, `sk-`, `password`, or `secret` indicators. It drops other keys and non-string values. This bounded filter is implemented; a full classification, sanitization, pseudonymization, exporter validation, and firewall-connected pipeline is not evidenced in this module.

The value heuristic is intentionally not described as complete sanitization. Producers must supply approved, non-sensitive metadata. Do not pass raw identifiers or arbitrary user-controlled labels.

## Event schema and classification

| Field/key | Type | Classification | Required policy |
| --- | --- | --- | --- |
| `operation` | String, max 64 chars | Public operational metadata | Allowlisted, bounded, coarse value only |
| `provider` | String, max 64 chars | Public operational metadata | Allowlisted; no account or credential data |
| `model_family` | String, max 64 chars | Public operational metadata | Coarse family only; not an arbitrary model/user label |
| `outcome` | String, max 64 chars | Public operational metadata | Coarse outcome only |
| `mode` | String, max 64 chars | Public operational metadata | Approved mode value only |
| `component` | String, max 64 chars | Public operational metadata | Approved component value only |
| User/tenant/session/task identifiers | Not in current allowed schema | Sensitive identifier | Exclude; approved correlation must use purpose-specific HMAC pseudonyms |
| Prompt/tool/model content, secrets, raw exception text | Not in current allowed schema | Sensitive content/secret | Never emit or export |

Unknown schema fields are denied by the target contract. The current tag-filter implementation enforces key membership and simple value checks, not a full event schema.

## Target processing pipeline

```text
Producer (metadata-only)
  -> classification and policy (unknown = deny)
  -> bounded allowlist / sanitization
  -> optional approved domain-separated HMAC pseudonymization
  -> export schema and privacy validation
  -> privacy firewall / egress gate
  -> external exporter only when explicitly allowed
```

This describes the required target architecture. Only source creation and the allowlisted tag filter are evidenced in `OpenConnect.Observability`; do not claim the remaining stages are currently integrated there.

## Firewall, detectors, and failure behavior

Current shared contracts include `FirewallMode` values `StrictLocal`, `LocalOnly`, `PolicyControlled`, `Disabled`; `FirewallFailureAction` values `Deny`, `LocalOnlyIfVerified`; `SecurityDetectorKind` values `Rules`, `GlinerPii`, `DebertaInjection`, `Qwen3Guard`, `PaddleOcr`, `YuNet`; `SecurityCoverage` values `Complete`, `Partial`, `Unavailable`; and `CoverageUnavailableAction` values `Deny`, `AllowLocalOnly`. Enum JSON strings are kebab-case and integer values are not accepted.

`PrivacyOptions` defaults to `Enabled=true`, `Mode=StrictLocal`, `OnFailure=Deny`; rules default enabled, while other detectors default disabled. DeBERTa injection detection and Qwen3Guard are optional/non-mandatory. The target observability export path must use the privacy firewall/egress gate and fail closed on absent or failed coverage. Local-only handling is permitted only when verified by policy. Current source provides `IPrivacyFirewall` and `EgressGate`, but no inspected evidence shows `Instrumentation` or a telemetry exporter wired through them; this integration remains a synchronization item.

## Pseudonymization contract

Where an explicitly approved diagnostic use requires correlation, pseudonymize identifiers with keyed HMAC. A domain-separation label must bind the digest to its specific purpose (and tenant boundary where required); never reuse a pseudonym across unrelated purposes. Key material must come from secret management and must not be hardcoded, logged, or exported. Rotation must be planned and tested, including the intended correlation window; no raw identifier fallback is allowed. No HMAC implementation or rotation contract was found in the inspected observability source, so these requirements are pending, not current behavior.

## Configuration contract

Current privacy configuration is under `OpenConnect:Privacy`; `OpenConnectOptions.SectionName` is `OpenConnect`. Relevant current defaults and enum values are described in [Observability requirements](observability-requirements.md#configuration). There is no implemented observability-specific configuration section evidenced in the inspected source. In particular, no exporter enablement, pseudonymization key, HMAC rotation, classification, or export-validation setting should be assumed or configured from this document. Never commit secret values.

## Metering boundary

The `OpenConnect.Metering` source defines `ModelUsageEvent`, normalized token/request quantities, cost state and provenance, `IModelUsageMeter`, and `IUsageLedger`. Metering validates nonnegative quantities, cache usage not exceeding input, and null cost for unknown cost. The in-memory ledger inserts atomically by event ID, treats an identical duplicate as a no-op, and rejects conflicting duplicates. Preserve these metering semantics; do not remove or silently redact the authoritative metering record to satisfy telemetry minimization. Do not replicate metering records into general diagnostics without a separate approved schema/policy.

## Required verification matrix

| Test class | Required assertion |
| --- | --- |
| Dimension filter | Only the six approved keys and bounded safe strings survive; unsupported key/type/length and heuristic secret indicators do not. |
| Classification and policy | Unknown classifications deny; absent policy does not allow export. |
| Firewall integration | Export is blocked on unavailable/failed policy or detector coverage; only explicitly verified local-only operation can continue locally. |
| Optional detector behavior | DeBERTa and Qwen3Guard are not mandatory for metadata-only events; their failure cannot authorize external egress. |
| HMAC properties | Domain separation prevents cross-purpose linkage; rotation follows the approved window; no raw identifiers or key material appear in telemetry. |
| Export validation | Reject unrecognized fields and unsafe synthetic values before exporter invocation. |
| Synthetic-secret acceptance | A fake marker such as `SYNTHETIC_SECRET_DO_NOT_EXPORT` is absent from exported output and causes rejection or approved safe redaction. |
| Metering regression | Usage normalization, unknown-cost representation, cache subset checks, idempotency, and conflicting duplicate rejection remain unchanged. |

These are required test classes, not claims that tests have executed or currently exist. No code tests are part of this documentation assignment.

## Existing integration migration

Existing consumers should migrate custom telemetry to the six-key allowlist and coarse values; remove content, secrets, raw identifiers, high-cardinality labels, and raw exception details. Review every exporter and its egress path; an exporter previously configured directly must not be considered approved by default. Correlation requiring identifiers needs an approved, secret-managed HMAC implementation and domain label before activation. Keep usage metering on its dedicated ledger path. Validate migration using synthetic-secret acceptance tests before any rollout; do not backfill old raw identifiers into exported telemetry.

## Decision log

| Decision | Rationale | Status |
| --- | --- | --- |
| Metadata-only and deny-by-default telemetry | Minimize collection and prevent accidental content egress. | Approved assignment requirement; implementation partially evidenced. |
| Keep an exact bounded dimension allowlist | Stable, low-cardinality operational diagnostics without arbitrary labels. | Implemented for `AllowlistedTags`. |
| Treat DeBERTa and Qwen3Guard as optional | Telemetry safety must not rely on optional detectors being installed. | Approved requirement; verify against final privacy implementation. |
| Use keyed, domain-separated HMAC only for approved correlation | Prevent plain-hash reversibility and cross-purpose linkage. | Required design; implementation not evidenced. |
| Keep metering separate from diagnostic telemetry | Preserve billing/audit semantics while minimizing general telemetry. | Metering structures are implemented; export separation is a policy requirement. |

## Implementation references

- `src/OpenConnect.Observability/Instrumentation.cs`
- `src/OpenConnect.Abstractions/Enums.cs`
- `src/OpenConnect.Core/Options.cs`
- `src/OpenConnect.Core/Validator.cs`
- `src/OpenConnect.Privacy/PrivacyContracts.cs`
- `src/OpenConnect.Privacy/Gates/EgressGate.cs`
- `src/OpenConnect.Metering/`
- `.kiro/specs/openconnect-core/requirements.md` (REQ-312)
