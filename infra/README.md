# Local infrastructure

Run these commands from the repository root. Copy `infra/.env.example` to
`infra/.env` and set a local PostgreSQL password before starting services.
The Compose file reads `infra/.env`; it does not use the example file as a
fallback. Keep `infra/.env` untracked and never share its values.

```sh
docker compose -f infra/compose.yml up -d
docker compose -f infra/compose.yml down
```

PostgreSQL is available on host port `5864` by default (container port `5432`);
NATS client connections use host port `4322` (container port `4222`) and its
monitoring endpoint uses port `8222`. Override the mapped database port with
`POSTGRES_PORT` and the mapped NATS client port with `NATS_PORT` in
`infra/.env`. PostgreSQL data persists in the named `pgdata` volume after
`down`; `down -v` removes it and should be used only when intentionally deleting
local database data. NATS JetStream is enabled; stream and subject provisioning
for `oc.v1.*` remains an out-of-band follow-up.
