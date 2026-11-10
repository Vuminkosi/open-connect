# OpenConnect E2E Sample — `mms-tts` (`workers/mms-tts`)

Standalone end-to-end sample and acceptance test suite for the Python MMS TTS worker (`workers/mms-tts`), covering `WSE2E-AT-052` through `WSE2E-AT-058` and the 12-scenario runtime model cache protocol.

## 1. Exact Image Build Command

```sh
docker build -f workers/mms-tts/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-mms-tts:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/mms-tts/compose.e2e.yaml up -d
sh samples/workers/mms-tts/scripts/smoke.sh
sh samples/workers/mms-tts/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Bearer token (`Authorization: Bearer ...`) |
| `OC_WORKER_ENGINE` | `fake` | `fake` for CI/offline contract tests, `real` for production |
| `MMS_OPT_IN_LANGUAGES` | `eng,spa` | Explicit opt-in language set (max 2 languages allowed) |
| `OC_MODEL_PROVISIONING` | `disabled` | `disabled`, `first-start-if-missing`, or `preprovisioned-only` |
| `OC_MODEL_CACHE_DIR` | `/models` | Path to mounted `oc_mms_models` volume |
| `OC_MODEL_OFFLINE` | `true` | Prevents remote weights downloads |
| `PORT_MMS_TTS` | `8087` | Host loopback port mapped to container `:8080` |

## 4. Expected Capabilities & Licensing Model

- **Opt-In Languages (`WSE2E-AT-052..053`)**: Synthesizes 16kHz mono audio only for opted-in language codes (e.g. `eng`, `spa`). Unapproved languages are rejected immediately without network calls (`language_not_opted_in`).
- **Commercial Licensing Gate (`WSE2E-AT-055`)**: Checkpoints under `CC-BY-NC-4.0` refuse commercial use with HTTP 503 (`license_blocked`).
- **LRU RAM Cache (`WSE2E-AT-057`)**: Checkpoints are cached in RAM only, bounded by `MMS_MAX_LOADED_CHECKPOINTS` (default 2), with zero disk writes.

## 5. Offline Test Procedure

```sh
sh samples/workers/mms-tts/tests/t1_contract.sh
sh samples/workers/mms-tts/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Unapproved language (`WSE2E-AT-053`)**: POST request with non-opted-in language -> HTTP `422` (`language_not_opted_in`).
- **Commercial policy denial (`WSE2E-AT-055`)**: Request commercial synthesis with CC-BY-NC checkpoint -> HTTP `503` (`license_blocked`).
- **Text overflow (`WSE2E-AT-056`)**: POST text >2000 characters -> HTTP `422` (`input_too_long`).

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/mms-tts/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_mms_models volume):
docker compose -f samples/workers/mms-tts/compose.e2e.yaml down --remove-orphans

# Destructive cache reset (ONLY when explicitly purging cached models):
docker volume rm oc_mms_models
```

## 9. Production Differences

Production runs with `OC_ENVIRONMENT=Production`, `OC_WORKER_ENGINE=real`, `OC_MODEL_PROVISIONING=preprovisioned-only`, preloaded checkpoints in `/models`, and mTLS/private network ingress.
