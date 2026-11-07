# Observability requirements

**Status:** Design requirements; implementation is partial. This document is for maintainers integrating telemetry and privacy controls. It does not assert that every requirement below is implemented.

## Goals and privacy boundary

Observability must help diagnose system behavior without turning telemetry into a secondary channel for prompts, tool arguments, user identifiers, credentials, or other sensitive content. Collection is deny-by-default: emit only bounded, explicitly approved metadata. The .NET host owns the privacy decision; no worker or telemetry exporter may bypass it. External egress is permitted only after the applicable privacy policy allows it and the exported representation has passed validation.

### Data classifications

| Classification | Examples | Observability treatment |
| --- | --- | --- |
| Public operational metadata | Operation, component, outcome, firewall mode | Allowed only through the bounded dimension allowlist; values are strings of at most 64 characters and sensitive-looking values are rejected. |
| Sensitive identifiers | User, tenant, session, task, event or attempt identifiers | Exclude from ordinary telemetry. If a future approved use requires linkage, pseudonymize with keyed HMAC and a purpose-specific domain; never emit the raw identifier. |
| Sensitive content | Prompts, completions, tool inputs/outputs, images, raw exception details | Never emit in telemetry. Sanitization is not permission to collect content. |
| Secrets | Tokens, passwords, keys, connection strings | Never collect, persist, export, or include in diagnostic dimensions. |

Classification is the policy input, not a substitute for enforcing the allowlist and export checks. Unknown or unclassified data is denied.

## Approved event dimensions

The current `OpenConnect.Observability.Instrumentation.AllowlistedTags` implementation accepts only the following keys, string values no longer than 64 characters, and values rejected by its sensitive-value heuristic (`@`, `bearer `, `sk-`, `password`, or `secret`, case-insensitive where implemented):

| Key | Intended meaning | Current implementation |
| --- | --- | --- |
| `operation` | Bounded operation name | Allowlisted |
| `provider` | Provider name | Allowlisted |
| `model_family` | Coarse model family, not an arbitrary model identifier | Allowlisted |
| `outcome` | Coarse result | Allowlisted |
| `mode` | Execution/privacy mode | Allowlisted |
| `component` | Component name | Allowlisted |

Other keys, non-string values, overlong strings, and values caught by the heuristic are dropped. This heuristic is a defense-in-depth filter, not a general-purpose secret detector; callers must not pass sensitive values. Do not add high-cardinality or user-derived dimensions without an approved contract and privacy review.

## Collection and export pipelines

The intended pipeline is:

1. Producers create metadata-only events; content and raw identifiers are not telemetry fields.
2. Classification and policy determine whether a field is eligible.
3. The bounded dimension allowlist filters keys and values.
4. Where explicitly approved, identifiers are pseudonymized before any durable or external boundary.
5. Export validation rejects fields or records that violate the privacy contract.
6. The egress gate consults the privacy firewall before external transmission; missing or failed policy/detector coverage denies egress (or allows local-only processing only when explicitly verified by policy).

Steps beyond the allowlisted tag filter are required design behavior, not established by the current observability implementation. Do not treat this list as evidence that a complete telemetry exporter pipeline exists.

## Privacy firewall and detector integration

The shared privacy contract defines `FirewallMode` (`StrictLocal`, `LocalOnly`, `PolicyControlled`, `Disabled`) and `FirewallFailureAction` (`Deny`, `LocalOnlyIfVerified`). `PrivacyOptions` defaults to enabled, `StrictLocal`, and `Deny`; the rules detector is enabled by default. DeBERTa injection detection and Qwen3Guard safety classification are optional detectors and must not become mandatory for telemetry acceptance. A missing, failed, or insufficient detector must fail closed for external egress; it must not be interpreted as a clean result. Local-only processing may proceed only under the explicit verified-local policy path.

The observability source currently exposes `Gateway`, `Firewall`, `GenAI`, `Agents`, `Workers`, `Vault`, and `Metering` activity sources plus the `OpenConnect` meter. These are diagnostic sources, not evidence that the firewall currently wraps every telemetry exporter. `MetadataOnly` is a constant; it does not by itself enforce metadata-only collection.

## Pseudonymization requirements

If identifier-level correlation is approved, use keyed HMAC rather than plain hashing. Include a distinct, stable domain-separation label for each purpose and tenant scope as specified by the approved privacy design; do not reuse a digest across unrelated purposes. Keys must be supplied through the host's secret-management mechanism, never hardcoded or logged. Rotation must support a controlled transition between key versions without silently making old identifiers linkable under a new purpose. Raw identifiers and keys must not leave the trusted host boundary. No pseudonymization implementation is currently documented as present in `OpenConnect.Observability`; treat this section as pending implementation/contract synchronization.

## Export validation and failure behavior

Before external export, validate the complete record against the approved schema and allowed classifications, reject unknown fields and raw sensitive values, and apply the privacy policy. On validation failure, detector unavailability, or policy uncertainty, do not export; record only a safe bounded outcome locally where possible. Do not fall back to raw telemetry. Export validation and telemetry-specific firewall binding are not evidenced by the current observability source and remain required work.

## Configuration

The current application configuration contract is rooted at `OpenConnect:Privacy` (see `OpenConnectOptions.SectionName`). Its implemented defaults are:

| Setting | Type | Default | Notes |
| --- | --- | --- | --- |
| `Enabled` | Boolean | `true` | Privacy firewall enabled by default. |
| `Mode` | `FirewallMode` | `strict-local` | Enum JSON values use kebab-case. |
| `OnFailure` | `FirewallFailureAction` | `deny` | Fail-closed default. |
| `Rules.Enabled` | Boolean | `true` | Rules detector defaults on. |
| `GlinerPii.Enabled`, `DebertaInjection.Enabled`, `Qwen3Guard.Enabled`, `PaddleOcr.Enabled`, `YuNet.Enabled` | Boolean | `false` | Optional detectors default off. |

Detector options also expose `ModelId`, `Revision`, `ModelDirectory`, `Device` (default `cpu`), `LoadPolicy` (default `startup`), `MaxConcurrentRequests` (default `1`), `MemoryBudgetMb`, and `Threshold`. These are privacy detector settings, not observability/exporter settings. No observability-specific JSON configuration contract is currently implemented. Never put key material in checked-in JSON.

Illustrative current privacy settings (not a complete observability exporter configuration):

```json
{
  "OpenConnect": {
    "Privacy": {
      "Enabled": true,
      "Mode": "strict-local",
      "OnFailure": "deny",
      "Rules": { "Enabled": true },
      "DebertaInjection": { "Enabled": false },
      "Qwen3Guard": { "Enabled": false }
    }
  }
}
```

## Metering and telemetry separation

Usage metering remains a business/audit record and must not be discarded merely to reduce diagnostic telemetry. `ModelUsageEvent` carries provider/model, normalized usage, cost state, and provenance; `IUsageLedger.AppendAsync` is idempotent by `EventId` and conflicting duplicate content fails. Unknown cost remains null, and cache usage is constrained as a subset of input. These metering records are not permission to copy provider/model identifiers or usage records into general-purpose telemetry. Apply their own access, retention, and export policy.

## Security and privacy verification

Required test classes for the completed design:

- Allowlist tests: accepted keys only; reject unknown keys, non-string values, values over 64 characters, and sensitive-looking synthetic values.
- Classification/policy tests: unknown classification denies; deny-by-default and failure behavior hold.
- Firewall integration tests: external egress is blocked when coverage is missing, detector/policy fails, or validation rejects; optional DeBERTa and Qwen3Guard are not prerequisites for ordinary metadata.
- Pseudonymization tests: deterministic within one key/purpose, unlinkable across domains, key rotation behavior, and no raw identifier/key leakage.
- Export contract tests: unknown fields and synthetic secret markers are rejected; safe metadata passes only when policy allows.
- Metering regression tests: usage, cost-state, provenance, cache-subset rules, idempotency and conflict behavior remain intact.

Synthetic-secret acceptance test: submit a deliberately fake marker such as `SYNTHETIC_SECRET_DO_NOT_EXPORT` through every telemetry field and export path; assert it is absent from exported output and that the record is rejected or safely redacted according to the approved policy. This is a test fixture only, not a real credential.

## Migration notes for existing integrations

1. Inventory telemetry exporters, custom tags, and persisted diagnostic records before enabling the new contract.
2. Remove prompts, completions, tool payloads, raw exceptions, and user/tenant/session identifiers from ordinary telemetry. Replace dimensions with the approved bounded keys and coarse values.
3. If correlation is essential, obtain approval for the purpose and scope, provision a secret-managed HMAC key, and use domain-separated pseudonyms. Do not migrate historical raw identifiers into new exports.
4. Route external export through policy and validation; do not treat existing exporter connectivity as authorization.
5. Keep metering on its separate ledger path and validate its existing semantics independently.
6. Test with synthetic secret markers before rollout. Existing integrations must be considered incompatible until reviewed against these controls.

## Implementation references

- `src/OpenConnect.Observability/Instrumentation.cs`
- `src/OpenConnect.Abstractions/Enums.cs`
- `src/OpenConnect.Core/Options.cs`
- `src/OpenConnect.Privacy/PrivacyContracts.cs`
- `src/OpenConnect.Privacy/Gates/EgressGate.cs`
- `src/OpenConnect.Metering/`
- `.kiro/specs/openconnect-core/requirements.md` (REQ-312)
