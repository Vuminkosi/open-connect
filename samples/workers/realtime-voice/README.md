# OpenConnect E2E Sample — `realtime-voice` (`workers/realtime-voice`)

Standalone end-to-end sample and acceptance test suite for the Python Realtime-voice orchestration worker (`workers/realtime-voice`), covering `WSE2E-AT-036` through `WSE2E-AT-044` and the 12-scenario runtime model cache protocol.

## 1. Exact Image Build Command

```sh
docker build -f workers/realtime-voice/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-realtime-voice:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/realtime-voice/compose.e2e.yaml up -d
sh samples/workers/realtime-voice/scripts/smoke.sh
sh samples/workers/realtime-voice/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Bearer token (`Authorization: Bearer ...`) |
| `OC_WORKER_ENGINE` | `fake` | Explicit fake engine mode for CI/offline orchestration |
| `OC_REALTIME_ORCHESTRATION_ONLY` | `true` | When true, no local model weights are required |
| `OC_MODEL_PROVISIONING` | `disabled` | `disabled`, `first-start-if-missing`, or `preprovisioned-only` |
| `OC_MODEL_CACHE_DIR` | `/models` | Path to mounted `oc_realtime_voice_models` volume |
| `OC_MODEL_OFFLINE` | `true` | Prevents remote weights downloads |
| `PORT_REALTIME_VOICE` | `8085` | Host loopback port mapped to container `:8080` |

## 4. Expected Capabilities & Session / Epoch Fencing Model

- **Session State Machine (`WSE2E-AT-036`)**: Transitions across `idle` -> `listening` -> `transcribing` -> `reasoning` -> `speaking` -> `listening`.
- **Speech-Epoch Fencing (`WSE2E-AT-037`)**: When a barge-in occurs, the worker monotonically increments `speech_epoch` and sets state to `interrupted`. In-flight speech synthesis chunks bearing stale epochs are dropped immediately.
- **Audio Boundaries (`WSE2E-AT-039..040`)**: Resampling 24kHz to 16kHz, Silero 512-sample frame accumulation with 64 context size and 256 recurrent state size, sequence gap detection.

## 5. Offline Test Procedure

```sh
sh samples/workers/realtime-voice/tests/t1_contract.sh
sh samples/workers/realtime-voice/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Barge-in interruption (`WSE2E-AT-037`)**: POST `barge-in` event -> increments epoch and invalidates earlier epoch work.
- **Stale epoch rejection (`WSE2E-AT-037`)**: POST event with obsolete `speechEpoch` -> rejected with `StaleEpoch` (HTTP 409).
- **Out-of-order transition (`WSE2E-AT-041`)**: POST event invalid for current state -> rejected with `InvalidTransition` (HTTP 409).

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/realtime-voice/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_realtime_voice_models volume):
docker compose -f samples/workers/realtime-voice/compose.e2e.yaml down --remove-orphans

# Destructive cache reset (ONLY when explicitly purging cached models):
docker volume rm oc_realtime_voice_models
```

## 9. Production Differences

Production runs with `OC_ENVIRONMENT=Production`, `OC_WORKER_ENGINE=real`, `OC_REALTIME_ORCHESTRATION_ONLY=false` when local VAD is active, and authoritative session management coordinated by the .NET gateway.
