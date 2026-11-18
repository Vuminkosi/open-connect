# Security Worker — End-to-End Sample (`samples/workers/security/`)

## Overview

End-to-end sample and release gate for the OpenConnect **Security** worker (`WSE2E-IMP-008`, `WSE2E-IMP-009`, `WSE2E-REQ-021–026`, `WSE2E-AT-001–008`).

- **Production Dockerfile & Compose:** `workers/security/Dockerfile`, `workers/security/compose.yaml`
- **Canonical Image Repository:** `vuminkosimatibe/openconnect-worker-security` (built locally as `vuminkosimatibe/openconnect-worker-security:e2e-local` during PR/CI runs)
- **Named Model Cache Volume:** `oc_security_models` mounted at `/models:ro`
- **Security Boundary:** The Python worker detects spans and labels only; it **never** authorizes requests or holds the .NET AES-GCM vault key. When a policy requires a detector that is disabled or unready, the .NET firewall fails closed with **zero outbound bytes** to external model or tool sinks.

## Build & Compose Invocation

```bash
# 1. Verify no build-time model downloads
sh ../shared/scripts/verify-no-build-models.sh

# 2. Build local E2E image from production Dockerfile
docker compose -f compose.e2e.yaml build --pull=false

# 3. Validate Compose configuration (T0 static)
sh ../shared/scripts/run-one.sh --worker security --tier t0

# 4. Run offline contract, policy-negative, and 12-scenario cache suite (T1 contract)
sh ../shared/scripts/run-one.sh --worker security --tier t1

# 5. Start container and verify bounded readiness (T2 image-startup)
sh ../shared/scripts/run-one.sh --worker security --tier t2

# 6. Run live container request/response + fail-closed negative suite (T3 integration)
sh ../shared/scripts/run-one.sh --worker security --tier t3
```

## Environment Variables & Secrets Configuration

Copy `../shared/env.example` to `.env` for local overrides (never commit real secrets):

| Variable | Default (E2E) | Description |
|---|---|---|
| `PORT_SECURITY` | `8080` | Loopback-bound host port mapped to container `:8080` |
| `OPENCONNECT_SCAN_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Private bearer token authenticating gateway-to-worker requests |
| `OC_WORKER_ENGINE` | `real` | Engine mode (`real` for rules-only or verified model; `fake` for CI stubs) |
| `OC_MODEL_DEVICE` | `cpu` | Device selector (`auto`, `cpu`, `cuda`) |
| `OC_MODEL_PROVISIONING` | `first-start-if-missing` | Startup model provisioning policy |
| `OC_MODEL_CACHE_DIR` | `/models` | Path to mounted `oc_security_models` volume |
| `OC_MODEL_OFFLINE` | `true` | Enforces zero outbound registry connections |
| `OC_SECURITY_RULES_ENABLED` | `true` | Deterministic CPU regex/checksum detector (requires zero model weights) |
| `OC_SECURITY_GLINER_ENABLED` | `false` | Optional semantic PII detector (requires pinned manifest & license) |
| `OC_SECURITY_DEBERTA_ENABLED` | `false` | Optional prompt-injection detector (requires pinned manifest & license) |
| `OC_SECURITY_QWEN3GUARD_ENABLED` | `false` | Optional safety classifier (requires pinned manifest & license) |
| `OC_SECURITY_PADDLEOCR_ENABLED` | `false` | Optional image OCR detector (requires pinned manifest & license) |
| `OC_SECURITY_YUNET_ENABLED` | `false` | Optional face bounding-box detector (requires pinned manifest & license) |

## Expected Capabilities & Model Manifest Status

- **`rules-only` mode (Default):** Advertises `["rules"]` at `GET /health/ready`. Requires **no** model checkpoints and makes **zero** model registry connections (`WSE2E-AT-001`–`004`: **PASS** in T1/T2/T3).
- **Model-backed detectors (`gliner-pii`, `deberta-injection`, `qwen3guard`, `paddle-ocr`, `yunet`):** Disabled by default. Requesting an unprovisioned detector returns `HTTP 503 {"detail": "detector unavailable"}` so the host blocks external egress (`WSE2E-AT-005`: **NOT_RUN / BLOCKED** for T4 model-live until approved checkpoint manifests are mounted).

## Fixtures (`fixtures/`)

- `request.json` / `expected.json`: Baseline T1/T3 rules scan contract fixture.
- `rules-pii.json`: Synthetic email, phone, and secret token spans with exact character offsets.
- `unicode-normalization.json`: Combining marks, CJK/RTL prefixes, and multi-codepoint emoji offset alignment.
- `overlap-priority.json`: Deterministic overlap priority resolution (`block > redact > mask > review > allow`).
- `prompt-injection.json`: Direct and indirect prompt injection payloads requiring `deberta-injection` fail-closed handling.
- `malicious-detector-output.json`: Compromised worker responses (`allow: true`, `grantedTools`, out-of-range/reversed spans) rejected by host schema/policy.
- `face-and-ocr.png`: Synthetic PNG containing a face region, OCR text region, and a `tEXt` EXIF canary chunk to verify pixel/metadata scrubbing before egress.
- `unsupported-image.bin`: Invalid binary container rejected before image decode.
- `forged-token.json`: Cross-tenant and workload-identity vault token restoration negative cases.
- `policy-coverage.yaml`: Declarative host privacy policy coverage matrix.

## Offline Cache & Failure-Injection Commands

```bash
# Run the 12-scenario runtime model cache protocol (offline reuse, revision upgrade, rollback, corruption, lock)
sh scripts/run-cache-matrix.sh

# Inject missing-detector request (asserts HTTP 503 fail-closed)
curl -i -X POST "http://127.0.0.1:${PORT_SECURITY:-8080}/scan" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer ${OPENCONNECT_SCAN_TOKEN:-e2e-test-token-do-not-use-in-prod}" \
  -d '{"schemaVersion":"openconnect.v1","requestId":"123e4567-e89b-42d3-a456-426614174020","text":"test","detectors":["rules","gliner-pii"]}'
```

## Evidence Filenames & Safe Teardown

- Evidence artifacts are written by `../shared/scripts/collect-evidence.sh` to `../shared/evidence/<stamp>/security.log` and `result.json` (`openconnect.samples.workers.evidence/v1`).
- **Safe Teardown (preserves named volume `oc_security_models`):**
  ```bash
  docker compose -f compose.e2e.yaml down --remove-orphans
  ```
  Never use `docker compose down -v` in ordinary teardown so verified model caches and post-mortem volumes are preserved.

## Production Differences

In production (`workers/security/compose.yaml`), the container runs on an internal workload network with mTLS/secret-mounted bearer tokens, no host port published (`ports:` omitted), and `OC_ENVIRONMENT=Production`.
