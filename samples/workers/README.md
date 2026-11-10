# Workers e2e samples (T0–T3)

End-to-end harness for the 11 OpenConnect worker images. Each sample builds
**only** its canonical worker Dockerfile (`workers/<name>/Dockerfile`, never a
toy image) with a local `:e2e-local` tag. No model downloads, no registry
pushes, no secrets.

T4 model-live is **BLOCKED / NOT_RUN** everywhere: no approved model assets.

## Prerequisites

- Docker with compose v2 (for T0 config checks and T2/T3 bring-up)
- `sh`, `curl`, `python3` on PATH (T1 is fully offline)
- Copy `shared/env.example` to `.env` beside a compose file to override
  ports/tokens (test-only dummy values)

## Layout

| Sample | Canonical image source | Status |
|--------|------------------------|--------|
| `security/` | `workers/security/Dockerfile` | READY — reference implementation (Dockerfile present in-repo) |
| `classification/` | `workers/classification/Dockerfile` | BLOCKED — pending worker delivery |
| `nllb/` | `workers/nllb/Dockerfile` | BLOCKED — pending worker delivery |
| `inference/` | `workers/inference/Dockerfile` | BLOCKED — pending worker delivery |
| `transcription/` | `workers/transcription/Dockerfile` | BLOCKED — pending worker delivery |
| `realtime-voice/` | `workers/realtime-voice/Dockerfile` | BLOCKED — pending worker delivery |
| `kokoro/` | `workers/kokoro/Dockerfile` | BLOCKED — pending worker delivery |
| `mms-tts/` | `workers/mms-tts/Dockerfile` | BLOCKED — pending worker delivery |
| `navigator-playwright/` | `workers/navigator-playwright/Dockerfile` | BLOCKED — pending worker delivery |
| `operational-agents/` | `workers/operational-agents/Dockerfile` (or `src/OpenConnect.WorkerHosts.*/Dockerfile`) | BLOCKED — pending worker delivery |
| `outbox/` | `workers/outbox/Dockerfile` (or `src/OpenConnect.WorkerHosts.*/Dockerfile`) | BLOCKED — pending worker delivery |
| `voice-vad-client-server/` | reuses `transcription` image — **no 12th image** | BLOCKED — pending transcription worker |
| `all-workers/` | aggregate compose of all 11 | T0-runnable (config only) |
| `shared/` | harness scripts, deps compose, env example, fixtures, CI sketch | READY |

Each worker sample contains `compose.e2e.yaml`, `README.md`, `fixtures/`,
`tests/t1_contract.sh`, and `scripts/smoke.sh`.

## Quickstart

```sh
# T0 static checks for every sample
for f in samples/workers/*/compose.e2e.yaml; do docker compose -f "$f" config >/dev/null && echo "PASS T0 $f"; done

# T1 contract fixtures (offline) for one worker / all workers
sh samples/workers/shared/scripts/run-one.sh --worker security --tier t1
sh samples/workers/shared/scripts/run-all.sh --tier t1

# T2 readiness / T3 fixture e2e (needs docker daemon + worker image build)
sh samples/workers/shared/scripts/run-one.sh --worker security --tier t3

# Guardrails: no models, no pushes, no secrets, no `down -v`
sh samples/workers/shared/scripts/verify-no-build-models.sh

# Sanitized evidence bundle
sh samples/workers/shared/scripts/collect-evidence.sh --tier t1
```

## Executable contract

- Unknown `--worker` / `--tier` / `--provider` values are rejected (exit 2);
  `--tier t4` is explicitly rejected as BLOCKED.
- Each run uses a separate compose project (`ocw-e2e-<worker>`); parallel runs
  never share networks, volumes, or container names.
- Waits are bounded (`READY_TIMEOUT_SEC`, default 120s); failures exit loudly.
- Logs are sanitized (Bearer tokens, `token`/`apiKey` fields, `SCAN_TOKEN`
  redacted) before display or capture under `shared/evidence/`.
- Ordinary teardown is `down --remove-orphans` — never `down -v`.

## Evidence

Per-run sanitized bundles land in `shared/evidence/<UTC-stamp>/` (git-ignored
by default; attach to the handoff instead of committing). Current status and
per-worker notes live in each sample README; T4 stays NOT_RUN until approved
model assets exist.

## CI

`.github/**` is owned by another worker, so the workflow sketch lives at
`shared/ci/workers-samples-e2e.yml` (T0+T1 per PR, T2/T3 on demand, T4 skipped).
