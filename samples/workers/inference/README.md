# OpenConnect E2E Sample — `inference` (`workers/inference`)

Standalone end-to-end sample and acceptance test suite for the Python Inference worker (`workers/inference`), covering `WSE2E-AT-021` through `WSE2E-AT-028` and the 12-scenario runtime model cache protocol.

## 1. Exact Image Build Command

```sh
docker build -f workers/inference/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-inference:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/inference/compose.e2e.yaml up -d
sh samples/workers/inference/scripts/smoke.sh
sh samples/workers/inference/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_INFERENCE_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Primary bearer token (`Authorization: Bearer ...`) |
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Fallback bearer token |
| `OC_WORKER_ENGINE` | `fake` | `fake` for CI/offline contract tests, `real` for production |
| `OC_INFERENCE_MODEL` | `muse-spark-1.3` | Model identity for capability discovery |
| `OC_INFERENCE_API_BASE_URL` | `""` | Optional external cloud API URL (opt-in only) |
| `OC_PRIVACY_ZONE` | `strict-local` | When `strict-local`, external provider egress is blocked |
| `OC_MODEL_PROVISIONING` | `disabled` | `disabled`, `first-start-if-missing`, or `preprovisioned-only` |
| `OC_MODEL_OFFLINE` | `true` | Prevents remote weights downloads |
| `PORT_INFERENCE` | `8083` | Host loopback port mapped to container `:8080` |

## 4. Expected Capabilities & Model Manifest / Licensing Status

- **Capabilities (`/internal/v1/capabilities`)**: Truthfully reports `baseCapabilities` (`chat`, `stream`). Optional capabilities (`tools`, `structured-output`, `embeddings`) are advertised only when both enabled and a model identity is configured (`WSE2E-AT-021`).
- **Streaming Contract (`WSE2E-AT-024`)**: Chunks have contiguous `seq` starting at 0, content deltas carry `done: false`, and exactly one final terminal chunk carries `done: true` with an empty delta.
- **Privacy Firewall Gate (`WSE2E-AT-028`)**: Strict-local privacy zone terminates cloud egress with HTTP 503 `privacy_blocked`.
- **Production Guardrail (`WSE2E-AT-027`)**: Fake engine requests in `OPENCONNECT_ENV=production` are rejected with HTTP 503 `fake_not_allowed_in_production`.

## 5. Offline Test Procedure

```sh
sh samples/workers/inference/tests/t1_contract.sh
sh samples/workers/inference/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Context overflow (`WSE2E-AT-022`)**: POST request with >64 messages or >4096 tokens -> HTTP `422`.
- **Unsupported embeddings (`WSE2E-AT-026`)**: Request `embeddings` when disabled -> HTTP `422` (`capability_unavailable`).
- **Fake engine in production (`WSE2E-AT-027`)**: Set `OPENCONNECT_ENV=production` + `OC_WORKER_ENGINE=fake` -> HTTP `503` (`fake_not_allowed_in_production`).
- **Privacy block (`WSE2E-AT-028`)**: Set `OC_PRIVACY_ZONE=strict-local` with cloud URL -> HTTP `503` (`privacy_blocked`).

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/inference/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_inference_models volume):
docker compose -f samples/workers/inference/compose.e2e.yaml down --remove-orphans

# Destructive cache reset (ONLY when explicitly purging cached models):
docker volume rm oc_inference_models
```

## 9. Production Differences

Production runs with `OC_ENVIRONMENT=Production`, `OC_WORKER_ENGINE=real`, `OC_MODEL_PROVISIONING=preprovisioned-only`, configured provider endpoints with privacy gates enforced host-side, and no mock tokens.
