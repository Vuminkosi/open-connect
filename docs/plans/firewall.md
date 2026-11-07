# OpenConnect — Privacy Firewall and Security Worker Specification

**Document:** `firewall.md`  
**Version:** 1.1-draft | **Status:** Normative implementation target; no feature is considered implemented without test evidence  
**Owner:** Security / framework runtime / Python worker maintainers  
**Companions:** `spec.md`, `implementation.md`, `checklist.md`, `configuration.md`  
**Traceability:** OC-FW-001–OC-FW-064; IMP-017 and IMP-080–IMP-089; AT-008 and AT-021–AT-030

## 0. Goal and non-negotiable boundaries

OpenConnect MUST ship a **privacy firewall enabled by default** when an application registers `services.AddOpenConnect()`. Its detection engine is an **optional self-hosted Python security worker** delivered as a separate Docker image, but the .NET enforcement boundary **is mandatory when privacy is enabled**. Installing a NuGet package MUST NOT silently launch Docker or download models. At startup, strict mode MUST refuse protected external inference when an authorized, healthy, local detector is unavailable. The application MAY explicitly disable privacy through trusted host configuration, without disabling authentication, tenant isolation, authorization, tool approvals, schema validation, or audit protections.

The core design separates **policy and enforcement** (trusted .NET host) from **classification/detection** (isolated Python worker). A compromised worker must not be able to authorize a tool call, resolve tokens, change a privacy policy, grant access, or select a remote endpoint. Private prompt data MUST NOT be published directly to NATS JetStream. Only controlled, authenticated point-to-point worker transport inside a documented trusted processing zone may carry plaintext for local scanning. The security worker MUST NOT have PostgreSQL credentials or decrypt the token vault.

**Pseudonymization is not anonymization.** Detection quality is measured, not presumed; even masked content may reveal sensitive information through context. Every publicly claimed supported language or media modality requires its own passing evaluation evidence. Do not copy an external repository's code, model weights, datasets, or benchmark results into the product without an approved license/provenance review; original implementation is the default.

### 0.1 Source-of-truth and agent execution rules

- Normative product behavior: this document plus `spec.md`. Work order: `implementation.md`. Evidence: `checklist.md`.
- Agents implement one `IMP-*` work package per pull request, write negative security tests before implementation, and record exact CI/test artifacts.
- Security boundaries are host-owned and must not be delegated to model-generated text, Markdown instructions, a client-provided tenant ID, or worker metadata.
- No passing mock unit test is evidence of real OCR, NER, audio redaction, production encryption, or a published image.
- Do not put names or code snippets from external research projects in product runtime source or public-facing docs. Maintain required source and model license notices in the dedicated legal inventory if actual third-party material is incorporated; legal obligations override a preference to omit credits.

## 1. Deployment and logical design

```text
Client / SDK / MiniComposer / authorized tool
                 |
        ASP.NET Core OpenConnect host
        + AddOpenConnect(): privacy ON
        + policy evaluator and egress interlock
        + tenant / principal / session resolver
        + local encrypted token vault adapter
                 |
      ┌──────────┴───────────┐
      |                      |
 Text/JSON/tool boundary     Image/audio/document boundary
      |                      |
  Authenticated LOCAL security-worker channel
      |                      |
      +---- Python Security Worker (Docker, opt-in deployment)
            - rules, checksums, secrets detection
            - pinned on-device NER
            - optional local OCR, local image segmentation
            - per-modality status and readiness
            - no vault keys, no authz or external inference authority
      |                      |
      +---- detections / masks / confidence / policy-relevant diagnostics
                 |
      .NET policy engine: validate -> tokenize -> encrypt mappings
                 |
   outbound egress gate -> approved model / tool / worker endpoint
                 |
   scoped response restoration -> validated client projection
```

The security worker is different from the `classification` worker: classification labels application content for the user's task; security detects protected entities and proposes spans to mask. Neither classification results nor LLM output may override an egress decision.

**OC-FW-001:** `OpenConnect.Privacy` supplies `IPrivacyFirewall`, immutable policy evaluation, egress interlocks, redaction result models, restoration gates, and auditable decisions without bundling Python models.

**OC-FW-002:** The `openconnect-worker-security` image contains Python 3.12+ and follows `workers/common` conventions: `uv.lock`, Pydantic contracts, nonroot execution, pinned base image, model provenance, liveness, true readiness and structured redacted logs.

**OC-FW-003:** `.AddOpenConnect()` registers the lead agent and firewall even if the security worker is not running. Missing required detector causes `privacy_dependency_unavailable` / `privacy_blocked` before external egress, NOT implicit disable.

**OC-FW-004:** Host always retains policy, token issuance and resolution, tenant authority and final allow/deny decision. Worker output is advisory detection data, verified against original input bounds before use.

**OC-FW-005:** Strict-local means unmasked content never leaves the operator-controlled trusted boundary for remote OCR, model classification, remote speech processing, hosted detection or telemetry. An operator-hosted local network worker can receive originals if authenticated and inside the documented trust zone. A remotely hosted worker is NOT strict-local even if self-managed, absent explicit trust-zone declaration and data-location approval.

**OC-FW-006:** Host and worker are independently versioned by contract major; incompatible worker versions are unavailable, not auto-downgraded.

### 1.1 Deployment modes

| Mode | Detector location | External model egress | Missing detector | Allowed default |
|---|---|---|---|---|
| `strict-local` | Local rules; optional healthy local NER or other detectors when explicitly required by coverage policy | Only sanitized payload | Block / optionally route to verified local-only processing with explicit policy | **Yes** |
| `local-only` | Local detection | No remote model egress | Local application may fail or remain local; never silently bypass required policy | Optional |
| `policy-controlled` | Local by default; approved additional detectors only after explicit boundary disclosure | Policy-defined, auditable | Configured deny or approved local-only route | Explicit opt-in |
| `disabled` | None required | Ordinary model flow | Privacy enforcement bypassed by trusted config only | **No** |

No request header, client-side setting, agent instruction, or worker result may select `disabled` or weaken a tenant's deployment policy.

## 2. Host registration and configuration contracts

```csharp
// Proposed API; verify actual Microsoft Agent Framework and DI types when coding.
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true; // default
    options.Privacy.Mode = PrivacyMode.StrictLocal;
    options.Privacy.OnFailure = PrivacyFailureAction.Deny;
    options.Privacy.RequireLocalSecurityWorker = true;
});

builder.Services.AddOpenConnectSecurityWorker(options =>
{
    options.BaseAddress = new Uri("https://security-worker.internal/");
    options.RequireMutualTls = true;
    options.Timeout = TimeSpan.FromSeconds(8);
});
```

```json
{
  "OpenConnect": {
    "Privacy": {
      "Enabled": true,
      "Mode": "strict-local",
      "OnFailure": "deny",
      "Detectors": ["rules"],
      "ProtectedEntities": ["person", "email", "phone", "address", "id", "financial", "secret"],
      "Restore": { "Default": "deny", "AllowFinalUserText": true },
      "SecurityWorker": {
        "Required": true,
        "Endpoint": "https://security-worker.internal/",
        "Protocol": "openconnect.security/v1"
      }
    }
  }
}
```

Secrets, mTLS identities and encryption keys are resolved from host secret stores, never from `agent-runtime.md`, YAML prompts or checked-in configuration. Settings above are illustrative contracts subject to type/API verification.

**OC-FW-007:** `Enabled=false` MUST be explicit in server-owned config and logged only as a Boolean security-status event. A prominent diagnostic warning and metric MUST be emitted on startup and configuration changes. AuthN/AuthZ/tenant/tool policy remain active.

**OC-FW-008:** Startup configuration validation rejects `OnFailure=allow` in strict mode, unknown detector names, missing required local processing, forbidden model egress zones and unsupported image/audio modalities configured as mandatory.

**OC-FW-009:** Requests cannot expand the available entity categories, relaxation rules or permitted network destinations. Tenant policies may tighten global policy, never loosen it.

**OC-FW-010:** For each request, compute an immutable `PrivacyPolicySnapshot` with policy revision, tenant scope, principal scope, session, turn, modalities, destination class and restoration rules. Persist only safe identifiers/revisions in audit records.

## 2A. Optional detector matrix, typed configuration and coverage policy (v1.1 normative amendment)

This amendment supersedes earlier examples that imply NER is always enabled. Read `configuration.md` §§2–4 and 7 before implementing any `IMP-08*` work. **Privacy firewall enabled is not equivalent to all detector models enabled.**

| Detector / `SecurityDetectorKind` | Default | Pipeline | Decisions supported | Disabled behavior |
|---|---|---|---|---|
| `Rules` | ON | Text | `Mask`, `Redact`, `Block` for supported patterns | Rules never initialized if explicitly off; relevant coverage unavailable |
| `GlinerPii` | OFF | Text semantic PII | `Mask`, `Redact`, `Review` | No GLiNER memory, import-time checkpoint load or download |
| `DebertaInjection` | OFF | Text injection | `Block`, `Review`, mark untrusted (bounded metadata) | No injection-model claim |
| `Qwen3Guard` | OFF | Input/output text safety | `Allow`, `Block`, `Review` (policy-owned) | No model-safety claim |
| `PaddleOcr` | OFF | Images/documents OCR | Extract coordinates then apply rules/GLiNER | No image-text protection claim |
| `YuNet` | OFF | Face identification regions | Face blur/opaque mask per policy | No face-masking claim |

**OC-FW-049:** Each detector MUST have typed `DetectorOptions` (`Enabled`, model ID/revision/hash, `DetectorDevice`, `DetectorLoadPolicy`, concurrency, memory budget, timeout and any tested threshold). Public closed values MUST be enums; serializations use validated stable strings. Python Pydantic schemas and C# options MUST pass golden contract parity. Do not build a runtime that uses arbitrary detector class names from configuration.

**OC-FW-050:** Disabled detectors MUST NOT download weights, load Python model modules with side-effect allocations, initialize CPU/GPU sessions, reserve a model cache, or advertise capabilities. `LazyOnFirstUse` is allowed for selected detectors only if policy explicitly permits the cold-start readiness behavior, and external egress is held until required detector becomes READY.

**OC-FW-051:** Host MUST compute `requiredCoverage` and `achievedCoverage` per request modality, destination/trust zone, tenant data class and exact operation. `FirewallMode.StrictLocal` specifies local execution; it does **not** by itself guarantee semantic PII detection, injection resistance, safety moderation, or protected image processing. Missing required coverage => `Block`, a durable, human `Review` if provisioned, or a **verified and approved** local-only route. No downgrade from GLiNER to rules-only when policy requires semantic PII.

**OC-FW-052:** Python worker returns per-detector readiness and capability manifest with checkpoint hash, supported entities, languages, modality, privacy zone, resource state and contract version. Detector results are **signals** for deterministic host policy, not autonomous approvals. A GLiNER or DeBERTa score must not grant authority; thresholds need model-specific evaluation before stable claims.

**OC-FW-053:** For lightweight rules-only deployments, structured PII requests are allowed only if relevant policy accepts that scope. Unstructured external egress requiring freeform-person detection must block or remain locally processed. Off-by-default prompt injection/safety detectors mean those model-based protections are unprovided; independent tool validation and authorization remain always on.

**OC-FW-054:** Docker models must use pinned manifests, resource admission, startup profiles and opt-in local volumes. The security-worker image itself MUST remain independent from downloading every optional model. Tests inspect disabled-detector import paths, network calls, memory initialization and loaded capability list. No numeric memory requirement is a guarantee before measurement.

**OC-FW-055–064:** Apply typed options consistently across host privacy middleware, operational and interactive tasks, agent tool-output inspection, WS streaming, images, auditable `Review`, CI, GHCR images and docs. Preserve immutable policy snapshot/fencing across runtime detector reconfiguration, with current requests using their approved snapshot. Unknown enum/config version fails closed on protected paths. `Privacy.Enabled=false` remains explicit trusted-host-only override; it does not disable identity/authorization policy. Read `configuration.md` for operational/interactive principal separation and acceptance AT-031–042.


## 3. Detection pipeline and entity taxonomy

**OC-FW-011:** Pipeline is: validate content and size -> canonicalize without changing semantics -> extract visible text/structured fields -> deterministic validators (email, phone, payment cards, IDs, secrets, known-values) -> optional enabled local NER -> overlap/type resolution -> confidence/risk classification -> policy decision -> placeholder mapping -> outbound sanitized schema reconstruction -> second-pass egress validation.

**OC-FW-012:** Entity categories MUST include at minimum: `PERSON`, `EMAIL`, `PHONE`, `ADDRESS`, `DATE_OF_BIRTH`, `GOVERNMENT_ID`, `ACCOUNT_NUMBER`, `PAYMENT_CARD`, `IP_ADDRESS`, `SECRET`, `ORG_SENSITIVE`, and `CUSTOM`. Each detector reports `entityType`, start/end Unicode-safe offsets (contract defines unit), detector ID/version, confidence, and policy decision candidate. Never label a probabilistic score as a calibrated probability without calibration evidence.

**OC-FW-013:** Rules validate checksums and contextual indicators for appropriate ID formats, and use language-specific packs for high-precision regional identifiers. Keep rules deterministic and unit tested with synthetic positive/negative examples. Known values supplied by trusted host are scoped per request and never logged.

**OC-FW-014:** When enabled, NER is ON-DEVICE in the Python security worker. The pinned model, revision hash, tokenizer files, license, intended languages, supported platforms, model-load status and test corpus results must appear in the manifest. Rules-only mode MUST NOT advertise reliable free-form name detection. A model download at request time is forbidden.

**OC-FW-015:** Define precise offset semantics (`Unicode scalar offsets` is recommended after canonical normalization) and provide C#–Python golden fixtures for emoji, combining accents, multilingual text, repeated entities, substrings and overlapping spans. An out-of-bounds span, invalid type or detector contract violation fails the scan; do not trust worker-reconstructed text as authoritative.

**OC-FW-016:** Overlapping spans resolve deterministically based on policy severity, validated identifier type, detector quality and stable offset ordering. No nested placeholder corruption; preserve original punctuation and syntax when safely possible.

**OC-FW-017:** Detection for passwords, API keys, access tokens and credentials has highest egress severity. Secrets MUST NEVER be restored into model output, external tool arguments, client suggestions, logs or telemetry merely because a generic text-restoration policy is enabled.

**OC-FW-018:** Developer custom detectors implement a versioned interface plus tests, are hosted in the same trusted processing zone, cannot alter security policy and must declare supported language/modality. Failure of a required detector blocks the request.

## 4. Tokenization, vault and restoration

**OC-FW-019:** Issue unpredictable opaque placeholders (for example `[[OC_PERSON_7d9f2b...]]`, illustrative only), cryptographically random and sufficiently long to prevent guessing. Tokens include NO raw value, tenant ID, actual username, reversible encoding of a secret, or discernible stable account identifier. Prefer keyed lookup keys and collision-resistant random identifiers.

**OC-FW-020:** Store original values exclusively in an authorized encrypted vault adapter (minimum envelope encryption with authenticated encryption, e.g. AES-256-GCM via approved library and per-record random nonce). Key material comes from a KMS/secret provider; never return keys to the worker. Bind each mapping to tenant, principal, session, turn, entity classification, policy revision, creation/expiry and key version. No cross-scope token resolution.

**OC-FW-021:** Persistence MUST support transactionally consistent creation with admitted turns, TTL expiry, purge, key rotation, concurrency tests, and secure failure on vault unavailability. No plaintext token values in database read models, backups, Redis/NATS, traces or exception messages. Evaluate actual backup encryption separately.

**OC-FW-022:** Stability across turns is OPTIONAL only under a policy-controlled session alias map, still scoped to principal+tenant+session; default mappings remain turn-bound and must not be globally stable across customers.

**OC-FW-023:** Restoration is an explicit operation requiring an authorized destination and exact field-path allowlist. Restore in approved final user-visible response fields only after policy evaluation, never in system prompts, audit records or untrusted transport payloads. Treat tokens present in attacker-supplied input as inert literals; do not resolve unissued or unbound placeholders.

**OC-FW-024:** Tool restoration is schema-aware and only occurs immediately before a trusted, authorized tool invocation. Read/write/external tool classes have distinct policies. The tool broker independently authorizes the operation, validates canonical arguments and binds approvals to the approved values; no unmasked value may bypass the tool policy by being embedded in another field.

**OC-FW-025:** Streaming restoration must support placeholders split across arbitrary chunks, UTF-8 split boundaries and reconnect/replay. Buffer the smallest bounded prefix needed to disambiguate a token, cap maximum token bytes and stale-buffer time, reject malformed token-like sequences when policy requires, and never emit partial sensitive content before authorization. Persist user-visible stream projections only after safe restoration decision and with tenant-scoped protections.

**OC-FW-026:** The final response scanner MUST detect unmasked sensitive values not authorized for output; deny or redact unsafe spans. Models sometimes emit literal values they inferred independently; placeholder-only logic is insufficient.

## 5. Every trust boundary and data-flow policy

| Boundary | Scan before leaving? | Plaintext recipient | Restoration default | Failure behavior |
|---|---|---|---|---|
| Client ingress -> host | Validate, authorize, scan before processing outside trusted local boundary | Host only | n/a | Deny sensitive external tasks when required scan unavailable |
| Host -> external LLM | **Always** if enabled | None: masked request only | Never | Deny |
| Host -> local inference worker | Policy decision based on declared trusted zone | Permitted local worker | Never automatically | Deny or permitted local-only path |
| Host -> external tool | Schema-aware scan | Masked only unless explicit data-sharing approval | Deny | Deny |
| Host -> trusted local tool | Schema-aware scan | Only approved tool argument fields | Exact fields only | Deny |
| Tool response -> model | **Always** | Model receives sanitized result | Never | Deny |
| Host -> NATS JetStream | Sanitize before envelope creation | Metadata/reference only | Never | Deny publish |
| Host -> Python task worker | Validate capability, zone and purpose | Allowed only for trusted local worker | Never by worker itself | Deny |
| Generated response -> user | Output scan + field-allowlisted restoration | Authenticated owner only | Policy-controlled | Redact/deny |
| Telemetry/logs/audit | Structured sanitized events only | Safe metadata | Never | Drop unsafe event / fail protected flow if required |
| Component proposals/suggestions | Validate schema, entitlement and props | Sanitized metadata only | No | Reject proposal |

**OC-FW-027:** All MAF model invocations MUST pass through a single enforceable model egress adapter; direct raw provider SDK bypasses require explicit security review and should be forbidden by integration tests/architectural rules. Tool calls, agent handoffs, context providers, retrieval and optional prompt caching must use equivalent gates.

**OC-FW-028:** If a worker needs plaintext to do its legitimate local task (classification, STT, TTS), use the explicit zone policy and an authenticated host reference-fetch with short TTL and purpose-specific scope. Never send plaintext in NATS messages, URLs, error bodies or GET query parameters.

**OC-FW-029:** No destination is considered local solely because its hostname says `localhost` or because it uses Docker. Enforce registered endpoint identity, TLS, network policy, allowed namespaces and threat-modelled host/worker placement.

## 6. Images, documents, audio and multimodal handling

**OC-FW-030:** Image protection requires local OCR for text, local face/signature/document segmentation where enabled, redaction on ORIGINAL pixel data, and a follow-up local verification scan before any external image egress. Remove or normalize sensitive EXIF and embedded metadata. Rasterization, PDF embedded layers, thumbnails, previews and page images need separate analysis. If any required detector is unavailable, strict mode refuses to transmit the image. Never send originals to a remote detector under strict-local rules.

**OC-FW-031:** Support explicit content-type allowlists, decompression bomb limits, page/pixel/file-size quotas, format validation and safe media decoding in a sandbox. OCR misses and partial redactions are measurable failure classes; a visual rectangle over a PDF is not sufficient if hidden text remains extractable.

**OC-FW-032:** Recorded and live audio cannot be promised protected before remote STT unless the original audio remains inside the approved trusted zone until sensitive content is removed or redacted by a validated local audio pipeline. Transcribing remotely then masking the transcript DOES NOT satisfy strict-local. Treat speaker biometrics as sensitive where configured.

**OC-FW-033:** Real-time speech shall maintain turn/speech epochs, isolate chunks by user/session, prevent stale audio playback after interruption, and gate outbound audio/TTS text by the same policy. When strict real-time audio redaction is not yet technically validated, advertise capability as unavailable rather than pretending transcript masking suffices.

**OC-FW-034:** Multimodal capabilities are independently advertised (e.g., `security.text.v1`, `security.image-ocr.v1`, `security.image-face.v1`, `security.audio-transcript.v1`). A healthy text detector does not imply safe image or audio processing.

## 7. Security worker API and protocol

**OC-FW-035:** Security worker participates in capability registry with authenticated machine identity and short TTL, but its scan endpoint is point-to-point on a private authenticated host-to-worker channel. NATS can carry only safe scan-request notices/operational status with opaque refs; the host must retrieve sensitive input through protected transport. Prefer no broker route for latency-sensitive scans. If a durable worker path is required, it MUST preserve confidentiality, leases and ACK-after-host-commit semantics.

Proposed routes (not public client API):

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health/live` | Process liveness only |
| `GET` | `/health/ready` | Rules/NER/modality models loaded, auth and limits validated |
| `GET` | `/internal/security/v1/capabilities` | Signed/authorized capability and detector manifest |
| `POST` | `/internal/security/v1/analyze` | Classify bounded text or authorized content payload; no vault token issuance |
| `POST` | `/internal/security/v1/analyze-image` | Optional verified local image scan/redaction, content by protected upload/ref |

Representative request (synthetic only; no live secrets):

```json
{
  "schemaVersion": "openconnect.security/v1",
  "scanId": "scan_01",
  "policyRevision": "sha256:policy-revision",
  "contentType": "text/plain",
  "localeHint": "en",
  "text": "Contact Ada Demo at ada@example.test",
  "maxEntities": 128
}
```

Representative response:

```json
{
  "schemaVersion": "openconnect.security/v1",
  "scanId": "scan_01",
  "status": "completed",
  "detectorRevision": "rules-1+ner-pinned",
  "offsetUnit": "unicode-scalar",
  "entities": [
    {"start": 8, "end": 16, "entityType": "PERSON", "confidence": 0.97, "detector": "local-ner"},
    {"start": 20, "end": 36, "entityType": "EMAIL", "confidence": 1.0, "detector": "email-rule"}
  ],
  "warnings": []
}
```

**OC-FW-036:** Host assigns tenant/principal/session/turn authorization based on server context, not worker request fields. Worker does not receive a tenant-wide vault-access token. Avoid echoing raw text in responses. Text field example is for illustration of contract only; production logs MUST never record it.

**OC-FW-037:** Auth uses mutually authenticated TLS or equivalent managed workload identity, rotated credentials, allowlisted host origin and independently scoped machine permissions. Rate limits, maximum text bytes, max entities, CPU/memory limits, deadlines, circuit breaker and clean cancellation are mandatory. Reject unauthorized requests before parsing large payloads.

**OC-FW-038:** Detector response spans are validated against host-owned input; worker can be faulty or hostile. Reject duplicate/out-of-bounds/nonconforming entities and unknown detector revision. Never let worker claim `allowed=true` to override host policy.

**OC-FW-039:** Readiness requires actual loaded mandatory detector and model hash verification, not merely an HTTP process. Capability registry expires stale entries; a required failed worker blocks outbound operations.

## 8. Docker build, operations and performance

```text
workers/
  common/
  security/
    pyproject.toml
    uv.lock
    Dockerfile
    src/openconnect_security/
      app.py
      config.py
      schemas.py
      detect/{rules.py,ner.py,resolve.py,validators.py}
      media/{ocr.py,images.py,audio.py}
      health.py
    models/MANIFEST.json             # references hashes; weights by approved volume
    tests/{unit,contracts,security,integration}/
infra/
  compose.yaml                        # security profile, optional service deployment
  security/{README.md,policy.example.json,model-notices.md}
contracts/jsonschema/
  security-scan.v1.json
  security-scan-response.v1.json
```

**OC-FW-040:** Image tag: `ghcr.io/<owner>/openconnect-worker-security:<version>`. Use a nonroot multi-stage Docker build and pinned base digest, reproducible `uv` lock, mandatory filesystem restrictions and bounded shared-memory/resource limits. Model downloads occur through a separate approved provisioning command, with pinned SHA/revision and read-only mounted volume; no runtime auto-download.

**OC-FW-041:** Publish CPU variant first; optional GPU variant only after hardware smoke. Apply network egress restriction to detector container: no external outbound requests in strict-local deployment. Map only private/loopback networks; do not publish worker ports to the public internet. Encrypt host-to-worker traffic when crossing namespaces/hosts.

**OC-FW-042:** Health metrics may include latency bucket, detector version, failed scan count, blocked policy decisions and queue utilization; never content, entity values or reversible tokens. Set explicit SLO targets after actual measurement, not inherited from third-party benchmarks.

**OC-FW-043:** On startup missing model, wrong hash, unsupported language pack, incompatible worker schema, worker crash, timeout or circuit-open state makes required capability `unavailable`; maintain structured failure reason code without echoing prompt text.

## 9. Security and privacy verification / release gates

Run full deterministic CI on synthetic fixture data with no external network/model downloads. Keep large-model and image/audio live gates separate but mandatory before declaring those capabilities stable.

**OC-FW-044:** Required adversarial test matrix:

| Category | Test conditions | Required outcome |
|---|---|---|
| Entity detection | IDs, email, names, aliases, secrets, multilingual/Unicode/overlap | Deterministic spans; measured miss/overmask report |
| Privacy enforcement | Missing worker, exception, timeout, NER load failure, invalid spans | No unauthorized external egress; stable failure code |
| Vault | Cross-tenant/principal/session/turn guesses, token collision, replay, expiry, key rotation | Zero unauthorized restoration |
| Tool | JSON property injection, malicious nested args, model tool escalation, expired approval | Zero unauthorized tool execution/unmask |
| Streaming | Placeholder split at every offset, random chunk boundaries, reconnect, cancellation | No partial leakage, deterministic restored output |
| Messaging | Outbox/NATS/downstream logs with secret canaries; redelivery | Zero raw canaries in persisted/public envelopes |
| Worker | Forged identity, wrong signature, oversized request, drifted schema, stale registry | Reject and report unavailable |
| Media | OCR hidden PDF layers, EXIF, faces/signatures, malformed images, speech privacy | Deny unsupported/unsafe egress |
| Configuration | Agent/runtime text attempts to disable or modify policy | Denied; trusted server option only |
| Disabled mode | Firewall off but auth, tool permissions and tenant checks active | Zero security bypass |

**OC-FW-045:** Required benchmark methodology records dataset source/license, synthetic-vs-real provenance, languages, task mixes, entity-level full leakage, partial leakage, overmasking, latency p50/p95, CPU/RAM, false negatives/positives, model revisions, confidence intervals where useful and hardware. No benchmark reported as product performance until reproduced. Include finance and non-English blind fixtures because performance varies across categories/languages.

**OC-FW-046:** Stable text firewall gate requires zero deliberate canary transmission in threat-boundary suites, zero cross-scope restoration, zero authorization bypass, and an explicit per-language detection regression budget approved by maintainers. These are **test criteria**, not guarantees of universal zero PII leakage.

**OC-FW-047:** CI jobs: `security-worker-unit` (uv locked, Ruff, mypy, pytest), `security-worker-contracts` (C#/Python schema/Unicode offsets), `firewall-integration` (Postgres, authenticated local worker, controlled fake model sink, NATS leakage probes), `firewall-adversarial` (property/fuzz), `firewall-image` (build/publish image scans, SBOM, license), and manual/scheduled `firewall-live-model` for supported text/media model sets. Add required status checks for first five; real media features only released when their live validation passes.

**OC-FW-048:** Publishing: security-worker image included in GHCR version/digest manifest, SBOM, signed provenance and vulnerability scan. Verify pulling by digest on clean host, startup without model -> unready, with pinned approved detector -> ready, scan result -> host masking -> authorized restoration, and scan outage -> blocked egress. Publish security contracts with NuGet framework and npm SDK only where needed; never put NER weights or tokens in npm/NuGet.

## 10. Implementation work packages and dependencies

| Task | Exact deliverable | Dependencies | Acceptance |
|---|---|---|---|
| IMP-017 | Host .NET egress enforcement, policy snapshot, default-on DI, token/vault interfaces; later integrate security worker | IMP-010, 011, 016 | AT-001, 002, 008 |
| IMP-080 | Security threat model, contract JSON Schema, Python model/legal evaluation, component inventory | IMP-002–005 | Published schema, license decision, synthetic fixtures |
| IMP-081 | `workers/security` Python project with uv, liveness, readiness, mTLS, policy-independent scan API | IMP-030, 080 | AT-021 |
| IMP-082 | Rules, checksums, secrets, known-values, Unicode span resolver, opt-in local NER | IMP-081 | AT-022 |
| IMP-083 | .NET security-worker adapter + host-side validation, vault, reversible placeholders | IMP-017, 020, 081, 082 | AT-023 |
| IMP-084 | Model/tool/retrieval/worker/output/NATS gate and restoration, chunk-safe streaming | IMP-083, 014, 016, 024, 025 | AT-024, 025 |
| IMP-085 | Local image/OCR/document scanning and safe image output (experimental until validated) | IMP-082, 084 | AT-026 |
| IMP-086 | Audio/voice strict-mode enforcement and modality registry (no unproven audio redaction) | IMP-084, 035, 036 as needed | AT-027 |
| IMP-087 | Docker security profile, provisioning, net restrictions, health & model manifest | IMP-081, 082, 037–039 | AT-028 |
| IMP-088 | Cross-language, adversarial, benchmark and performance tests; CI required gates | IMP-082–087 | AT-029 |
| IMP-089 | Signed GHCR image, license/SBOM/provenance and deployed-consumer integration docs | IMP-074, 075, 079, 088 | AT-030 |

### 10.1 Detailed implementation handoff

1. Implement `security-scan.v1.json` and response schema with Python and C# parity fixtures. Fix offset units and invalid-data policy before building detection.
2. Implement worker process with a synthetic rule-based detector first, strict resource quotas, identity/auth, and readiness false without any required detector.
3. Add deterministic rules, checksums and entity resolution. Pin a local NER checkpoint only after provenance/license review and truthfully limit supported languages.
4. Add the host's immutable policy snapshot, encrypted token vault, C# scan client and egress interlock. Prove no outbound HTTP/MAF model invocation when scanner unavailable.
5. Add tool and model boundary adapters; treat outbound tools, tool results, memory/context, web retrieval, subscriptions and event payloads as separate egress surfaces.
6. Add streaming restoration using adversarial chunk split property tests, then real WS reconnect/authorized replay tests.
7. Implement optional local media features independently. No remote image/OCR calls under strict mode; refuse unavailable media tasks rather than downgrade.
8. Wire optional worker Compose deployment and required host fail-closed readiness. Build container smoke and release digest verification.
9. Run synthetic canary suites against a controlled fake external model sink and event capture. File failures as blockers; do not simply lower detector confidence thresholds to make tests pass.
10. Publish operational docs explaining installation, model provision, threat boundaries, policy toggles, fallback restrictions and incident response; release only after evidence logs.

### 10.2 Required CLI examples (targets; verify once implemented)

```bash
# No model download in ordinary PR CI
cd workers/security
uv sync --locked
uv run ruff check .
uv run mypy src
uv run pytest -q

# Optional service; core package registration never starts Docker
cd ../..
docker compose -f infra/compose.yaml --profile security up -d security-worker

# Test .NET integration with disposable Postgres + NATS and local worker
# Actual script paths must be created during IMP-088.
dotnet test OpenConnect.slnx -c Release --filter Category=PrivacyFirewall
```

## 11. Test case IDs, durable evidence and status

| ID | Scenario | Status | Evidence |
|---|---|---|---|
| AT-021 | Authenticated worker contract, missing-model readiness and strict-mode blocking | NOT RUN | — |
| AT-022 | Local rules + optional NER when advertised, offset parity and multilingual regression | NOT RUN | — |
| AT-023 | Encrypted mapping, collision resistance, TTL and cross-tenant restoration denial | NOT RUN | — |
| AT-024 | External model/tool/worker/NATS egress intercepts + secret-canary exclusion | NOT RUN | — |
| AT-025 | Streaming split-token, interruption and reconnect authorized restoration | NOT RUN | — |
| AT-026 | Local OCR/image/PDF redaction and no remote original upload | NOT RUN | — |
| AT-027 | Audio never reaches remote STT unmasked under strict mode; unsupported blocked | NOT RUN | — |
| AT-028 | Docker nonroot, no model image layer, readiness/health and egress restriction | NOT RUN | — |
| AT-029 | Security fuzz/benchmark, no cross-scope restoration and CI fails on negative test | NOT RUN | — |
| AT-030 | Signed image digest, license/SBOM audit, fresh-host worker/host failure smoke | NOT RUN | — |

### Exit criteria

Stable **text** firewall requires AT-021, AT-023–025, AT-028–030, AT-008 and AT-039–040; AT-022 is additionally required when semantic NER is advertised; image and audio capabilities (AT-026/027) can remain experimental/unavailable, but MUST not be advertised as protected or enabled by default. Existing core P0 release tasks must not claim privacy-first production behavior until strict text detection and egress enforcement work with a real pinned local detector. A disabled firewall is a documented exception and never a default fallback.

**No checkboxes in `checklist.md` should be marked complete until test, review and release evidence actually exists.**
