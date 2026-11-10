# OpenConnect E2E Sample — `voice-vad-client-server` (§6.1)

Standalone end-to-end sample and architectural acceptance test suite for Client/Server Voice Voice Activity Detection (VAD) per `docs/plans/workers-samples-e2e-spec.md` §6.1 (`WSE2E-IMP-015`).

> [!NOTE]
> Per §6.1, there is **NO 12th worker image**. This sample uses the canonical Transcription worker image (`workers/transcription`) as the server-side STT backend, alongside client-side browser/Blazor VAD and optional host-side .NET server Silero VAD adapters.

## 1. Exact Image Build Command

```sh
docker build -f workers/transcription/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-transcription:e2e-local .
```

Zero model weights are downloaded or copied during `docker build`.

## 2. Exact `docker compose` Command

```sh
docker compose -f samples/workers/voice-vad-client-server/compose.e2e.yaml up -d
sh samples/workers/voice-vad-client-server/scripts/smoke.sh
sh samples/workers/voice-vad-client-server/tests/t1_contract.sh
```

## 3. Environment Variables & Secrets Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `OPENCONNECT_TRANSCRIPTION_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Primary bearer token |
| `OPENCONNECT_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Fallback bearer token |
| `OC_WORKER_ENGINE` | `fake` | `fake` for CI/offline contract tests |
| `OC_TRANSCRIPTION_MODEL` | `whisper-tiny` | Model identity |
| `PORT_TRANSCRIPTION` | `8084` | Host loopback port mapped to container `:8080` |

## 4. VAD Modes & Architectural Principles

- **Four Operating Modes**:
  1. `Disabled`: Continuous streaming, no VAD gating applied. Zero model downloads in server adapter.
  2. `Client`: Browser-side Silero ONNX / WebRTC VAD gating to conserve uplink bandwidth. Gating is a performance optimization only.
  3. `Server`: Server-side Silero ONNX VAD adapter active on the host gateway. When the realtime-voice worker is absent, server-only .NET VAD remains usable provided an authorized STT path is configured.
  4. `Hybrid`: Client-side coarse speech detection with server-side authoritative endpointing.
- **Pre-Roll & Audio Framing**: 250ms pre-roll buffer (4000 samples @ 16kHz) retained prior to speech start; 24kHz capture resampled to 16kHz (2:3 ratio); Silero 512-sample frame accumulation with 64-sample context and 256-element recurrent state.
- **Gap & Device Resets**: Sequence gaps and input device changes reset internal VAD state cleanly.
- **Barge-in Fencing & Single Commit**: Monotonic speech epochs fence audio; exactly one final transcript commits upon utterance completion.
- **Headless Node Import Safety**: Importing `@openconnect/client` in a Node/headless environment never accesses browser globals (`window`, `navigator`).

## 5. Offline Test Procedure

```sh
sh samples/workers/voice-vad-client-server/tests/t1_contract.sh
```

## 6. Failure-Injection Commands

- **Barge-in interruption**: Stale epoch work discarded; only the current epoch may commit.
- **Permission denial**: Audio capture denied by user/OS policy degrades gracefully to `PermissionDenied`.
- **Sequence gaps**: Abrupt timestamp/sample gaps trigger internal recurrent state reset.

## 7. Evidence Artifact Filenames

Written under `artifacts/workers-e2e/<run-id>/voice-vad-client-server/`:
- `build.log`, `compose-config.yaml`, `container-inspect.json`, `health-ready.json`, `capabilities.json`, `test-junit.xml`, `test-summary.json`, `cache-before.json`, `cache-after.json`, `network-isolation.txt`, `worker.log`.

## 8. Safe Teardown vs Destructive Reset

```sh
# Safe teardown (preserves oc_transcription_models volume):
docker compose -f samples/workers/voice-vad-client-server/compose.e2e.yaml down --remove-orphans

# Destructive cache reset:
docker volume rm oc_transcription_models
```

## 9. Production Differences

In production, client-side VAD runs inside the user's browser via WebAssembly / ONNX Runtime Web, while server-side VAD executes inside ASP.NET Core with mTLS and private network routing.
