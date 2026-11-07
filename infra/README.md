# Local infrastructure

Run these commands from the repository root. Copy `infra/.env.example` to
`infra/.env` and set a local PostgreSQL password before starting services.
The Compose file reads `infra/.env`; it does not use the example file as a
fallback. Keep `infra/.env` untracked and never share its values.

```sh
docker compose -f infra/compose.yml up -d
docker compose -f infra/compose.yml down
```

PostgreSQL (PostGIS 17) is available on host port `5874` by default; NATS
clients use host port `4332` (container port `4222`) and monitoring is bound
to loopback at host port `8322` (container port `8222`). These defaults avoid
host ports currently used by other local containers. Override them with
`POSTGRES_PORT`, `NATS_CLIENT_PORT`, and `NATS_MONITOR_PORT` in `infra/.env`.
Qdrant has no host port and is intended for access from the Compose network.
Set a unique local password for PostgreSQL, Qdrant, and both NATS users in
`infra/.env`; use `QDRANT_IMAGE` with the verified v1.19.0 image digest. The
bootstrap jobs create `OC_NOTIFICATIONS` / `oc.v1.notifications.inapp` and
`OC_INGESTION` / `oc.v1.ingestion.>` streams. Override their names/subjects
with the documented `NATS_*` variables. `OPENCONNECT_NATS_OPTIONS_FILE` is an
optional path available inside the bootstrap container; if it is unset or the
file is absent, the job uses environment configuration only. PostgreSQL,
Qdrant, and NATS data persist in named volumes; `down -v` deletes that local
data and should be used only intentionally.
