# Analytics inference worker

This worker provides deterministic single-feature summaries, robust median
absolute deviation anomaly screening and a seasonal-naive forecasting baseline.
It does not currently implement trained models or a durable task consumer.
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

For a Cloud worker deployment, set `OPENCONNECT_CLOUD_CONSUMPTION_ENABLED=true`
and configure `OPENCONNECT_CLOUD_AUTHORITY_ORIGIN`,
`OPENCONNECT_CLOUD_AUTHORITY_CA_FILE`, `OPENCONNECT_CLOUD_WORKER_CERT_FILE`, and
`OPENCONNECT_CLOUD_WORKER_KEY_FILE`. The file paths must be absolute and the
authority must use HTTPS. The private Cloud endpoint consumes a payload-bound,
single-use claim before calculation; the ordinary signed endpoint is disabled
in this mode. Readiness checks the local TLS configuration, not control-plane
reachability. Deployments must restrict access to the worker endpoint and use a
mutual-TLS proxy with deployment-specific credentials.

The Cloud gateway has a versioned analytics request contract and a payload
adapter that derives the worker tenant ID from the authenticated Cloud tenant.
The public Cloud execution route is opt-in and requires the authority,
deployment, worker TLS, privacy, claim and request-quota settings. Cloud
responses include the worker-measured elapsed calculation time, rounded up to
milliseconds; this excludes admission and transport and is not CPU time.
Completion records that measurement alongside request-count settlement.
Pricing and compute-duration quotas require separate policy configuration.
The route requires integration verification in the target deployment before
it is considered operational.

The `openconnect.v1` request includes `taskId`, `kind` (`analytics.analyze`),
`tenantId`, timezone-aware `issuedAt` and `expiresAt`, and a `payload` with
`feature`, `unit`, `lineage`, `values`, and optional `threshold`. A request
represents one homogeneous measurement unit; the caller must validate unit
and lineage. Up to 10,000 numeric or null samples and 256 KiB are accepted.
Outputs include input positions, coverage, algorithm version and warnings.
An insufficient baseline or zero median absolute deviation yields no anomaly
scores. Results are statistical signals, not automated decisions.

Set `payload.trend` to `true` to return a descriptive linear slope per sample
position. Sample positions must represent equally spaced observations. Missing
values retain their original positions, and fewer than three observations yield
no slope. This calculation does not establish causality or statistical
significance. The default response omits the trend result.

To request a forecast, include `payload.forecast` with timezone-aware
`timestamps` parallel to `values`, a fixed `intervalSeconds`, a caller-chosen
`seasonalPeriod` in samples (1–365), and `horizon` (1–100). Times are compared
as UTC instants. The worker returns no points for missing data, irregular time
steps, or insufficient history. The baseline repeats the latest seasonal cycle;
it evaluates chronological one-step holdout errors and compares them with a
non-seasonal naive baseline. Bounds use empirical holdout residual quantiles:
they are not calibrated prediction intervals or a guarantee of coverage.
The same one-step residual offsets are shown at every horizon; multi-step
uncertainty is not estimated and is explicitly flagged.
The result reports a warning for small holdouts and where the selected seasonal
period fails to improve holdout MAE. A seasonal period is supplied by the caller,
not inferred or statistically established by the worker. Results do not support
automated financial, medical or other adverse decisions.
