# openconnect-worker-common

Shared Python library for OpenConnect workers: wire contracts, security
primitives, transport helpers, and local model utilities.

- Runtime dependencies: `pydantic v2` + stdlib only.
- No database, vault, network, or model-download I/O on import. The explicit
  startup initializer may fetch operator-approved model assets over HTTPS.
- Non-root compatible: no privileged operations, no hard-coded absolute paths.
- Opt-in logging redaction scrubs recognized email, bearer, secret assignment,
  phone and card patterns from formatted messages and exception tracebacks.

## Layout

```text
src/openconnect_worker_common/
  contracts/  envelope, capability, errors, model_manifest (pydantic v2, forbid-extra)
  security/   identity (bearer auth, fail-closed), signing (HMAC-SHA256), redacted_logs
  transport/  nats (settings + subjects), task_claim, task_commit, private_stream
  models/     manifest, provision (plan only, no download), verify, resource_admission
  health.py   framework-free liveness/readiness + optional FastAPI wiring
```

Enum wire values are kebab-case (`security-scan`, `admit`, `completed`, ...).

## Usage

```python
from openconnect_worker_common.contracts.envelope import WorkerEnvelope
from openconnect_worker_common.security.identity import Identity
from openconnect_worker_common.security.redacted_logs import get_redacted_logger
from openconnect_worker_common.transport.task_claim import TaskClaim

logger = get_redacted_logger(__name__)

envelope = WorkerEnvelope.model_validate(payload)  # forbid-extra, expiry checked
if not Identity.from_environment().verify(authorization_header):
    ...  # deny: fail closed

claim = TaskClaim.from_envelope(envelope, worker_id="worker-1")
```

`get_redacted_logger` filters records created directly by that named logger.
Python does not apply a parent logger's filters to records propagated by child
loggers. To cover those records, attach the existing filter to **each**
application-owned handler that should redact them:

```python
from openconnect_worker_common.security.redacted_logs import RedactingFilter

handler.addFilter(RedactingFilter())  # handler configured and owned by the application
```

The filter sanitizes the record in place. It clears raw exception information
after rendering to prevent logging formatters from rendering it again, while
retaining sanitized traceback text. It does not configure global handlers,
inspect arbitrary structured extras, or guarantee redaction of records sent
to an unfiltered sink.

Health (framework-free):

```python
from openconnect_worker_common.health import liveness, readiness_status

liveness()                      # {"status": "live"}
readiness_status({"rules": True})  # (200, {...}) or (503, {...}) when degraded
```

FastAPI wiring (imports FastAPI lazily, only when called):

```python
from openconnect_worker_common.health import register_fastapi_routes

register_fastapi_routes(app, capabilities=["rules"])
```

## Environment

| Variable | Purpose | Default |
|---|---|---|
| `OPENCONNECT_WORKER_TOKEN` | Bearer token for private worker calls | unset (auth denies) |
| `OPENCONNECT_WORKER_SIGNING_KEY` | Hex-encoded HMAC-SHA256 key | unset (signing unavailable) |
| `OPENCONNECT_NATS_URL` | NATS server URL (`nats://` or `tls://`) | `nats://127.0.0.1:4222` |

## Checks

```bash
cd workers/common
uv sync --locked            # or: uv sync (first run writes uv.lock)
uv run pytest -q
uv run ruff check src tests
```

Live NATS access needs `nats-py` in the consuming worker; this package only
declares settings and subject conventions so imports never require it.

## Model initialization

Run `python -m openconnect_worker_common.models.initialize` only as an init
stage, with `OC_MODEL_MANIFEST_PATH` pointing to an administrator-controlled
JSON file and `OC_MODEL_CACHE_DIR` pointing to a persistent cache. The JSON
contains a pinned runtime `manifest`, a `sources` map keyed by asset path
(`url` and `mediaTypes`), `allowedHosts`, `resourceRequest`, `resourceBudget`,
`maxDownloadBytes`, `downloadTimeoutSeconds`, and optional `diskReserveBytes`,
`licenseAccepted`, and `commercialUse`. The revision must be a 40- or
64-character hexadecimal immutable digest; every asset needs a nonzero size
and SHA-256. Resource budgets must reflect the deployment's actual limits.
Only reviewed administrator-controlled sources and licenses may be selected.

`OC_MODEL_PROVISIONING=disabled` avoids reading the manifest and creating a
cache. `verify-existing-only` and `OC_MODEL_OFFLINE=true` forbid fetching;
the default `first-start-if-missing` permits approved HTTPS fetches. Serving
processes should mount the verified cache read-only and use
`verify_for_request`, never the initializer. The initializer checks every redirect
against the host and public-address policy and rejects private DNS addresses,
non-HTTPS sources, unapproved hosts, and digest or size
mismatches. It does not make a model available by itself: a consuming worker
still needs an approved manifest, model adapter, and readiness integration.
