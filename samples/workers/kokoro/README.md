# OpenConnect E2E Sample — `kokoro` (`workers/kokoro`)

Standalone end-to-end sample and acceptance test suite for the Python Kokoro TTS worker (`workers/kokoro`), covering `WSE2E-AT-045` through `WSE2E-AT-051` and the 12-scenario runtime model cache protocol.

## 1. Exact Image Build Command

```sh
docker build -f workers/kokoro/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-kokoro:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/kokoro/compose.e2e.yaml up -d
sh samples/workers/kokoro/scripts/smoke.sh
sh samples/workers/kokoro/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Bearer token (`Authorization: Bearer ...`) |
| `OC_WORKER_ENGINE` | `fake` | `fake` for CI/offline contract tests, `real` for production |
| `KOKORO_VOICES` | `af_heart,af_bella,am_adam` | Comma-separated allowlist of valid voice identifiers |
| `KOKORO_DEFAULT_VOICE` | `af_heart` | Default voice if unspecified in request |
| `OC_MODEL_PROVISIONING` | `disabled` | `disabled`, `first-start-if-missing`, or `preprovisioned-only` |
| `OC_MODEL_CACHE_DIR` | `/models` | Path to mounted `oc_kokoro_models` volume |
| `OC_MODEL_OFFLINE` | `true` | Prevents remote weights downloads |
| `PORT_KOKORO` | `8086` | Host loopback port mapped to container `:8080` |

## 4. Expected Capabilities & Synthesis Contract

- **Audio Format & Sample Rate (`WSE2E-AT-045`)**: Synthesizes 24kHz mono audio with `af_heart` (or configured voices).
- **Voice Validation (`WSE2E-AT-047`)**: Unknown voices are rejected with HTTP 422 (`unknown_voice`).
- **Dynamic Inventory (`WSE2E-AT-051`)**: Updating the `KOKORO_VOICES` list dynamically extends supported voices without code changes.
- **SSML/Markup Safety (`WSE2E-AT-050`)**: Markup tags in input text are treated strictly as plain characters, avoiding injection.

## 5. Offline Test Procedure

```sh
sh samples/workers/kokoro/tests/t1_contract.sh
sh samples/workers/kokoro/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Excessive input (`WSE2E-AT-046`)**: POST text >4096 characters -> HTTP `422` (`input_too_long`).
- **Unknown voice (`WSE2E-AT-047`)**: POST `invalid-voice.json` -> HTTP `422` (`unknown_voice`).
- **Empty input (`WSE2E-AT-048`)**: POST whitespace-only text -> HTTP `422` (`empty_input`).

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/kokoro/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_kokoro_models volume):
docker compose -f samples/workers/kokoro/compose.e2e.yaml down --remove-orphans

# Destructive cache reset (ONLY when explicitly purging cached models):
docker volume rm oc_kokoro_models
```

## 9. Production Differences

Production runs with `OC_ENVIRONMENT=Production`, `OC_WORKER_ENGINE=real`, `OC_MODEL_PROVISIONING=preprovisioned-only`, local ONNX models staged in `/models`, and mTLS/private network ingress.
