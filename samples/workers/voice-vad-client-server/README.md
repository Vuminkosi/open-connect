# Voice VAD client/server — e2e sample (T0–T3)

Status: BLOCKED pending worker delivery — reuses the `transcription` worker
image (`openconnect/transcription:e2e-local`); **no 12th image is built**.
The "client" is the host-side `scripts/smoke.sh` driving a VAD chunk sequence
(`speech-chunk` shaped fixtures); the "server" is the transcription worker.

T4 model-live: **BLOCKED / NOT_RUN** — no approved model assets.

## Flow (T3)

1. POST non-final chunks `fixtures/chunk-*.json` to `/transcribe` (partial).
2. POST `fixtures/chunk-final.json` (`isFinal: true`).
3. Assert the final response carries `schemaVersion`, `requestId`, `text`.

| Tier | Command |
|------|---------|
| T0 | `sh ../shared/scripts/run-one.sh`-equivalent: `docker compose -f compose.e2e.yaml config` |
| T1 | `sh tests/t1_contract.sh` |
| T2/T3 | `TIER=t3 sh scripts/smoke.sh` (bounded ready-wait, sanitized logs) |

Teardown is `docker compose down --remove-orphans` (never `down -v`).
