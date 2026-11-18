# navigator-playwright worker

Node/Chromium worker exposing an **allowlisted browser tool facade**. There is no
`browser_evaluate`, no arbitrary JS execution, no file access, and no direct
model surface. The .NET host grants authority per command; this worker enforces
that grant fail-closed.

## Tools (allowlisted)

| Tool | Purpose | Gate |
| --- | --- | --- |
| `observe` | Redacted snapshot of visible nodes | URL (if given) must be approved origin + public IP |
| `find-target` | Locate a target by query | Requires `query`; URL scoped as above |
| `navigate` | Go to a URL | Approved origin + http(s) + DNS resolves to public IP |
| `click` | Click an approved target | `targetId` in caller `approvedTargets`/`grant.targets` |
| `fill` | Fill an approved **non-secret** field | Approved target + secret fields denied (`privacy_blocked`) |
| `select` | Select an option on an approved target | Approved target + `value` required |
| `wait` | Bounded wait (0–10,000 ms) | Range-enforced |
| `capture` | Consented, redacted capture | Requires `consent: true`; output always redacted |

Denied (403 `tool_not_allowed`): `browser_evaluate`, `evaluate`, `eval`,
`script`, `exec`, `run-js`, `file-read`, `file-write`, `download`, `upload`,
and any other non-allowlisted tool name.

## Endpoints

- `GET /health/live` — no auth, `{status:"live"}`.
- `GET /health/ready` — no auth, `{status:"ready", capabilities:[...]}`.
- `GET /internal/v1/capabilities` — Bearer auth; worker/tool manifest.
- `POST /internal/v1/execute` — Bearer auth; `{tool, args}`. Only allowlisted
  tools execute; violations return stable error codes (`unauthorized`,
  `tool_not_allowed`, `approval_required`, `privacy_blocked`,
  `invalid_request`, `capability_unavailable`).

## Configuration

| Variable | Required | Description |
| --- | --- | --- |
| `NAVIGATOR_WORKER_TOKEN` (or `WORKER_AUTH_TOKEN`) | yes | Bearer token; missing token fails closed (503 on protected routes) |
| `NAVIGATOR_ALLOWED_ORIGINS` | yes (prod) | Comma-separated approved origins, e.g. `https://example.com` |
| `PORT` | no | Default `8080` |
| `PLAYWRIGHT_LAUNCH` | no | `1` in the container image (real Chromium); unset = simulator (tests/dev) |
| `PLAYWRIGHT_CHROMIUM_PATH` | no | Override Chromium executable path |

## SSRF / DNS-rebind guard

Every navigation-scoped URL must:

1. Use `http:`/`https:` (`file:`, `data:`, `javascript:`, `blob:`, `ftp:` denied).
2. Match an approved origin when `NAVIGATOR_ALLOWED_ORIGINS` is set.
3. Have a hostname that is not a blocked name (`localhost`, cloud metadata hosts).
4. Resolve — at request time — exclusively to public IPs. Loopback, RFC1918,
   link-local (incl. `169.254.169.254`), multicast, and reserved ranges are denied.

Known residual risk: a hostile DNS owner could rebind between the pre-request
resolution and the browser's own connection (TOCTOU). Mitigations in place:
per-request resolution (no caching), blocked-range enforcement, and approved-
origin scoping. Deployments facing adversarial domains should additionally pin
resolved IPs / run egress filtering at the network layer.

## Fills, secrets, and captures

- `fill` refuses secret-looking fields (`password`, `ssn`, `card`, `cvv`,
  `token`, `secret`, `api-key`, …), `fieldType: password`, or explicit
  `isSecret: true` with `privacy_blocked`.
- `capture` requires explicit consent (`args.consent: true` or
  `args.grant.consent: true`) and its text is always PII-scrubbed
  (emails/cards → `[redacted-…]`).

## Per-run profiles

Each `/execute` runs inside `withFreshProfile()`: a fresh `mkdtemp` dir under
`os.tmpdir()` (mounted as `tmpfs` in compose) that is `rm -rf`'d in a
`finally` block. Real Chromium launches receive it as their profile scope, so
cookies/storage never leak between runs. No model volume is mounted (this
worker ships no models).

## Run

```sh
npm ci --omit=dev
NAVIGATOR_WORKER_TOKEN=dev-token NAVIGATOR_ALLOWED_ORIGINS=https://example.com npm start
npm test
```

## Compose

```sh
NAVIGATOR_WORKER_TOKEN=… NAVIGATOR_ALLOWED_ORIGINS=https://example.com docker compose up --build
```
