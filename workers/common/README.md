# openconnect-worker-common

Shared Python library for OpenConnect workers: wire contracts, security
primitives, transport helpers, and local model utilities.

- Runtime dependencies: `pydantic v2` + stdlib only.
- No database, vault, network, or model-download I/O on import or in any helper.
- Non-root compatible: no privileged operations, no hard-coded absolute paths.
- Logging is redacted by default: emails, bearer tokens, secrets, phone
  numbers, and card numbers are scrubbed before they reach log output.

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
