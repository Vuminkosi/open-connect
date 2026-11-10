# Realtime voice worker - e2e sample (T0-T3)

Status: T0+T1 green, T3 green fake-labeled (orchestration-only session contract; engine fake; synthetic fixtures). T4 model-live: N/A (orchestration-only, no cloud path).

Canonical image source: `workers/realtime-voice/Dockerfile` (built as `openconnect/realtime-voice:e2e-local`).
T4 model-live: N/A (orchestration-only — no model weights, no model matrix).

## Tiers

| Tier | What | Command |
|------|------|---------|
| T0 | `docker compose config` static check | `sh ../shared/scripts/run-one.sh --worker realtime-voice --tier t0` |
| T1 | Contract fixture validation (offline) | `sh ../shared/scripts/run-one.sh --worker realtime-voice --tier t1` |
| T2 | Build + bounded `/health/ready` wait | `sh ../shared/scripts/run-one.sh --worker realtime-voice --tier t2` |
| T3 | Fixture-driven request/response | `sh ../shared/scripts/run-one.sh --worker realtime-voice --tier t3` |
| T4 | Model-live | BLOCKED - skipped, no approved assets |

Teardown is `docker compose down --remove-orphans` (never `down -v`).
Logs are sanitized (Bearer tokens / keys redacted) before display or capture.
Copy `../shared/env.example` to `.env` beside the compose file to override ports/tokens (test-only values).

## Session contract (orchestration-only, explicitly fake-labeled)

Session flow against `POST /internal/v1/execute` (auth `OPENCONNECT_WORKER_TOKEN`, engine `OC_WORKER_ENGINE=fake`):
`voice.session-open` -> `voice.event` (`begin-listening`, `transcript-final`, `begin-reasoning`, `begin-speaking` with epoch fencing).
Real execute responses carry `schemaVersion`/`taskId`/`kind` plus `state`/`speechEpoch` (and `result` with fake `chunks` for `begin-speaking`).
The single source of truth is the worker Dockerfile + its docs.
If the worker changes an endpoint, update `scripts/smoke.sh`,
`fixtures/request.json`, and `fixtures/expected.json` in this directory only.
