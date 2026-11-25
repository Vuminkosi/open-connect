# openconnect-imessaging-worker

Private real-time delivery, ephemeral presence, call signaling, and opaque push notification worker for OpenConnect IMessaging.

## Security Boundary

- **Zero plaintext**: Never holds participant private keys, never decrypts message content, and never accepts plaintext chat content, identities, URLs, or tokens in push wake-up payloads.
- **Fail-closed by default**: Disabled until `OC_IMESSAGING_ENABLED=true` and `OPENCONNECT_WORKER_TOKEN` + `OPENCONNECT_WORKER_SIGNING_KEY` (32+ bytes hex) are configured.
- **Authenticated private API**: All execution calls require `Authorization: Bearer <token>` and `X-OpenConnect-Signature: <hex HMAC-SHA256 over exact request bytes>` (with optional `X-OpenConnect-Key-Id` key rotation), bounded timestamp windows, and nonce replay protection.
- **In-app channel only**: Advertises only verified ready capabilities (`messaging.inapp.send`, `messaging.inapp.receive`, `messaging.inapp.call`, `messaging.device.enrollment`, and `messaging.push.fcm` / `messaging.push.webpush` strictly when configured). Never advertises WhatsApp, Telegram, or Discord.
- **No model volume**: Requires no ML models, GPU, or model downloads (`OC_MODEL_PROVISIONING=disabled`).

## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /health/live` | None | Process liveness probe |
| `GET /health/ready` | None | Fail-closed readiness probe (503 when disabled or unconfigured) |
| `GET /internal/v1/capabilities` | Bearer | Verified ready capability manifest (`openconnect.v1`) |
| `POST /internal/v1/execute` | Bearer + HMAC-SHA256 | Execute `messaging.route`, `messaging.presence`, `messaging.signal`, `messaging.push.dispatch` |
| `POST /internal/v1/relay/push` | Tenant Bearer + HMAC-SHA256 | Optional shared push-relay ingress (gated by `OC_IMESSAGING_RELAY_ENABLED=true`, opaque handles only) |

## NATS Subjects

- JetStream stream: `OC_MESSAGING` (`oc.v1.messaging.durable.>`)
- Core NATS live gateway fanout: `oc.v1.messaging.live.<tenant>.<gateway>`

## Local Verification

```bash
cd workers/imessaging
uv sync --locked
uv run ruff check .
uv run pytest -q
```
