# all-workers — aggregate e2e compose (T0–T3)

Runs all 11 worker samples side by side on distinct host ports
(see `../shared/env.example`). Each service builds from its canonical
`workers/<name>/Dockerfile` with a local `:e2e-local` tag only.

T4 model-live: **BLOCKED / NOT_RUN** — no approved model assets.

## Commands

```sh
# T0 static check
docker compose -f compose.e2e.yaml config >/dev/null && echo "PASS T0 all-workers"

# T2 full bring-up (bounded per-service healthchecks; needs docker daemon)
COMPOSE_PROJECT_NAME=ocw-e2e-all docker compose -f compose.e2e.yaml up --build -d

# Ordinary teardown (never `down -v`)
COMPOSE_PROJECT_NAME=ocw-e2e-all docker compose -f compose.e2e.yaml down --remove-orphans
```

Per-worker T1/T2/T3 remain in each `samples/workers/<worker>/` directory and
are driven by `shared/scripts/run-one.sh` / `run-all.sh` with separate
`ocw-e2e-<worker>` project names.
