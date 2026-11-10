# Navigator Playwright worker â€” e2e sample (T0â€“T3)

Status: BLOCKED pending worker delivery â€” Pending nav-play teammate delivery (Node worker).

Canonical image source: `workers/navigator-playwright/Dockerfile` (built as `openconnect/navigator-playwright:e2e-local`).
T4 model-live: **BLOCKED / NOT_RUN** â€” no approved model assets.

## Tiers

| Tier | What | Command |
|------|------|---------|
| T0 | `docker compose config` static check | `sh ../shared/scripts/run-one.sh --worker navigator-playwright --tier t0` |
| T1 | Contract fixture validation (offline) | `sh ../shared/scripts/run-one.sh --worker navigator-playwright --tier t1` |
| T2 | Build + bounded `/health/ready` wait | `sh ../shared/scripts/run-one.sh --worker navigator-playwright --tier t2` |
| T3 | Fixture-driven request/response | `sh ../shared/scripts/run-one.sh --worker navigator-playwright --tier t3` |
| T4 | Model-live | BLOCKED â€” skipped, no approved assets |

Teardown is `docker compose down --remove-orphans` (never `down -v`).
Logs are sanitized (Bearer tokens / keys redacted) before display or capture.
Copy `../shared/env.example` to `.env` beside the compose file to override ports/tokens (test-only values).

## Provisional contract

Endpoint paths and response fields below are provisional until the worker
lands; the single source of truth will be the worker Dockerfile + its docs.
If the worker changes an endpoint, update `scripts/smoke.sh`,
`fixtures/request.json`, and `fixtures/expected.json` in this directory only.
