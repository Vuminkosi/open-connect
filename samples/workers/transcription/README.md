# OpenConnect E2E Sample — `transcription` (`workers/transcription`)

Standalone end-to-end sample and acceptance test suite for the Python Transcription (STT) worker (`workers/transcription`), covering `WSE2E-AT-029` through `WSE2E-AT-035` and the 12-scenario runtime model cache protocol.

## 1. Exact Image Build Command

```sh
docker build -f workers/transcription/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-transcription:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/transcription/compose.e2e.yaml up -d
sh samples/workers/transcription/scripts/smoke.sh
sh samples/workers/transcription/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_TRANSCRIPTION_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Primary bearer token (`Authorization: Bearer ...`) |
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Fallback bearer token |
| `OC_WORKER_ENGINE` | `fake` | `fake` for CI/offline contract tests, `real` for production |
| `OC_TRANSCRIPTION_MODEL` | `whisper-tiny` | Model identity |
| `OC_TRANSCRIPTION_FILE_URL` | `""` | Optional external cloud STT URL (opt-in only) |
| `OC_PRIVACY_ZONE` | `strict-local` | When `strict-local`, external STT provider egress is blocked |
| `OC_MODEL_PROVISIONING` | `disabled` | `disabled`, `first-start-if-missing`, or `preprovisioned-only` |
| `OC_MODEL_OFFLINE` | `true` | Prevents remote weights downloads |
| `PORT_TRANSCRIPTION` | `8084` | Host loopback port mapped to container `:8080` |

## 4. Expected Capabilities & Model Manifest / Licensing Status

- **Audio Bounds & Validation (`WSE2E-AT-029..034`)**: Validates WAV headers (PCM format 1, 1-2 channels, 8kHz..48kHz sample rates). Reject corrupt containers (`invalid_wav_header`), unsupported codecs (`unsupported_mime_type`), duration overflow (`audio_too_long`), and channel/rate mismatches.
- **Privacy Firewall Gate (`WSE2E-AT-035`)**: Strict-local privacy zone terminates cloud STT egress with HTTP 503 `privacy_blocked`.
- **Stream Cutoff Partials (`WSE2E-AT-035`)**: Intermediate streaming partials are non-committing; exactly one final result commits upon stream completion.

## 5. Offline Test Procedure

```sh
sh samples/workers/transcription/tests/t1_contract.sh
sh samples/workers/transcription/scripts/run-cache-matrix.sh
```

## 6. Failure-Injection Commands

- **Unsupported codec (`WSE2E-AT-031`)**: POST `unsupported-codec.json` -> HTTP `422` (`unsupported_mime_type`).
- **Oversized audio (`WSE2E-AT-032`)**: POST audio exceeding max duration -> HTTP `422` (`audio_too_long`).
- **Corrupt WAV header (`WSE2E-AT-033`)**: POST `corrupt-container.json` -> HTTP `422` (`invalid_wav_header`).
- **Privacy block (`WSE2E-AT-035`)**: Set `OC_PRIVACY_ZONE=strict-local` with cloud URL -> HTTP `503` (`privacy_blocked`).

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/transcription/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_transcription_models volume):
docker compose -f samples/workers/transcription/compose.e2e.yaml down --remove-orphans

# Destructive cache reset (ONLY when explicitly purging cached models):
docker volume rm oc_transcription_models
```

## 9. Production Differences

Production runs with `OC_ENVIRONMENT=Production`, `OC_WORKER_ENGINE=real`, `OC_MODEL_PROVISIONING=preprovisioned-only`, local ONNX/Whisper model mounted in `/models`, and mTLS/private network ingress.
