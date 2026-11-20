# Analytics inference worker

This worker currently provides deterministic single-feature summaries and robust
median absolute deviation anomaly screening. It does not currently implement
forecasting, time-series analysis, trained models, or a durable task consumer.
It retains no submitted data and makes no model downloads.

Run `uv sync --locked`, `uv run ruff check .`, and `uv run pytest -q` from this
directory. The root Compose file offers an optional `analytics` profile. Set
`OPENCONNECT_WORKER_TOKEN` and `OPENCONNECT_WORKER_SIGNING_KEY` before starting
it. The signing key is hex encoded and must represent at least 32 bytes. The
service listens only on the Compose network; protect that network and secrets.

`GET /health/live` reports process liveness. `GET /health/ready` returns 503
until the bearer token and signing key are configured. A bearer token is
required for `GET /internal/v1/capabilities`. `POST /internal/v1/execute`
requires the same bearer token and `X-OpenConnect-Signature`: lowercase hex
HMAC-SHA256 of the exact UTF-8 JSON request bytes using the signing key.
The trusted host must authorize the tenant and sanitize data before signing;
the signature binds the tenant and payload but does not itself establish the
caller user's authorization. Requests must expire within five minutes. The
endpoint is stateless: it does not provide durable idempotency or replay
protection, and it must not be used as a durable job consumer.

The `openconnect.v1` request includes `taskId`, `kind` (`analytics.analyze`),
`tenantId`, timezone-aware `issuedAt` and `expiresAt`, and a `payload` with
`feature`, `unit`, `lineage`, `values`, and optional `threshold`. A request
represents one homogeneous measurement unit; the caller must validate unit
and lineage. Up to 10,000 numeric or null samples and 256 KiB are accepted.
Outputs include input positions, coverage, algorithm version and warnings.
An insufficient baseline or zero median absolute deviation yields no anomaly
scores. Results are statistical signals, not automated decisions.
