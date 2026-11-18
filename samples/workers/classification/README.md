# Classification Worker — End-to-End Sample (`samples/workers/classification/`)

## Overview

End-to-end sample for the OpenConnect **Classification** worker (`WSE2E-IMP-010`, `WSE2E-REQ-027–028`, `WSE2E-AT-009–014`).

- **Production Dockerfile & Compose:** `workers/classification/Dockerfile`, `workers/classification/compose.yaml`
- **Canonical Image Repository:** `vuminkosimatibe/openconnect-worker-classification` (built locally as `vuminkosimatibe/openconnect-worker-classification:e2e-local`)
- **Named Model Cache Volume:** `oc_classification_models` mounted at `/models:ro`
- **Advisory Boundary:** Classification outputs (`labels`, `scores`, `abstained`, `modelRevision`, `labelSetVersion`) are strictly **advisory** (`grantsTools: false`). Classifier responses can never grant tools, elevate caller scopes, or bypass host authorization.

## Build & Compose Invocation

```bash
# 1. Verify no build-time model downloads
sh ../shared/scripts/verify-no-build-models.sh

# 2. Build local E2E image from production Dockerfile
docker compose -f compose.e2e.yaml build --pull=false

# 3. Validate Compose configuration (T0 static)
sh ../shared/scripts/run-one.sh --worker classification --tier t0

# 4. Run offline contract, advisory policy, and cache suite (T1 contract)
sh ../shared/scripts/run-one.sh --worker classification --tier t1

# 5. Start container and verify bounded readiness (T2 image-startup)
sh ../shared/scripts/run-one.sh --worker classification --tier t2

# 6. Run live container request/response + negative suite (T3 integration)
sh ../shared/scripts/run-one.sh --worker classification --tier t3
```

## Environment Variables & Secrets Configuration

| Variable | Default (E2E) | Description |
|---|---|---|
| `PORT_CLASSIFICATION` | `8081` | Loopback-bound host port mapped to container `:8080` |
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Private bearer token authenticating gateway-to-worker requests |
| `OC_WORKER_ENGINE` | `fake` (CI) / `real` (Prod) | Engine selector (`fake` is explicitly labeled in CI; `real` requires verified checkpoint) |
| `OC_MODEL_DEVICE` | `cpu` | Device selector (`auto`, `cpu`, `cuda`) |
| `OC_MODEL_PROVISIONING` | `first-start-if-missing` | Startup model provisioning policy |
| `OC_MODEL_CACHE_DIR` | `/models` | Path to mounted `oc_classification_models` volume |
| `LICENSE_ACCEPTED` | `0` | Must be `1` when loading a checkpoint that requires license acceptance |
| `LABEL_SET_VERSION` | `v1` | Active operator-defined neutral label set (`v1` or `v2`) |

## Expected Capabilities & Model Manifest Status

- **Neutral Label Sets:** `v1` (`billing-question`, `technical-support`, `general-question`, `unknown`) and `v2` (adds `account-access`). Contains zero clinical/medical defaults.
- **T1–T3 Contract & Integration:** **PASS** (explicitly fake-labeled engine `engine: "fake"`).
- **T4 Model-Live (`WSE2E-AT-010`):** **NOT_RUN / BLOCKED** until an approved classifier checkpoint and `manifest.json` are mounted into `oc_classification_models` with `LICENSE_ACCEPTED=1`.

## Fixtures (`fixtures/`)

- `request.json` / `expected.json`: Baseline T1/T3 execute request and expected fields.
- `label-sets.json`: Operator-defined neutral label sets `v1` and `v2` and forbidden clinical labels list.
- `balanced-sentences.json`: Balanced utterances across `billing-question`, `technical-support`, and `general-question`.
- `conflicting-labels.json`: Multi-intent sentence verifying `maxLabels` cap and score calibration.
- `out-of-distribution.json`: Unsupported locale (`tlh`), ambiguous noise (`abstained: true`), and unknown `labelSetVersion` (`HTTP 422`).
- `invalid-scores-and-schema.json`: Out-of-bounds thresholds and unnormalized score negative fixtures.
- `forged-tool-grant.json`: Compromised classifier response attempting `grantsTools: true` and `toolGrant` injection.

## Offline Cache & Failure-Injection Commands

```bash
# Run offline manifest, checksum, license-denial, corruption, and rollback checks
sh scripts/run-cache-matrix.sh

# Inject unknown labelSetVersion (asserts HTTP 422 label_set_unknown)
curl -i -X POST "http://127.0.0.1:${PORT_CLASSIFICATION:-8081}/internal/v1/execute" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer ${OPENCONNECT_WORKER_TOKEN:-e2e-test-token-do-not-use-in-prod}" \
  -d '{"schemaVersion":"openconnect.v1","taskId":"neg-1","input":{"text":"hello","labelSetVersion":"no-such-set"}}'
```

## Evidence Filenames & Safe Teardown

- Evidence files: `../shared/evidence/<stamp>/classification.log` and `result.json`.
- **Safe Teardown (never `down -v`):**
  ```bash
  docker compose -f compose.e2e.yaml down --remove-orphans
  ```

## Production Differences

In production (`workers/classification/compose.yaml`), `OC_WORKER_ENGINE=real`, `model-init` verifies `/models/manifest.json` and checkpoint weights before `classification` starts, and no host ports are exposed outside the private runtime network.
