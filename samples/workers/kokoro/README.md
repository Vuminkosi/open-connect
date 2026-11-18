# OpenConnect E2E Sample — `kokoro` (`workers/kokoro`)

Offline contract checks for the current local-only Kokoro worker (`workers/kokoro`). T1 checks real WAV/PCM encoding, input validation, fixed voice configuration, epoch metadata, and literal pipeline input. It does **not** demonstrate model-live synthesis, speech quality, downstream stale-chunk disposal, or the runtime cache lifecycle.

The compose/model-live instructions below predate the real-engine migration and are not validated by T1. In particular, the current worker does not implement the old fake-engine/offline provisioning switches and accepts only `af_heart`. Do not use those switches as a guarantee that readiness or synthesis will avoid model downloads. Live execution requires separate review and authorization.

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

- **Audio Format & Sample Rate (`WSE2E-AT-045`)**: The real codec emits 24kHz mono WAV/PCM; T1 uses numeric codec input, not fabricated successful speech.
- **Voice Validation (`WSE2E-AT-047`)**: Unknown voices are rejected with HTTP 422 (`unknown_voice`).
- **Dynamic Inventory (`WSE2E-AT-051`)**: The supported voice is fixed to `af_heart`; attempts to expand `KOKORO_VOICES` fail configuration validation.
- **SSML/Markup Safety (`WSE2E-AT-050`)**: T1 verifies exact literal text is forwarded to a pipeline double and empty model output fails closed. It makes no model-level safety claim.

## 5. Offline Test Procedure

```sh
sh samples/workers/kokoro/tests/t1_contract.sh
sh samples/workers/kokoro/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Excessive input (`WSE2E-AT-046`)**: Text over 2000 characters is rejected by the real engine before model loading.
- **Unknown voice (`WSE2E-AT-047`)**: POST `invalid-voice.json` -> HTTP `422` (`unknown_voice`).
- **Empty input (`WSE2E-AT-048`)**: Whitespace-only text is rejected by the real engine before model loading.

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
