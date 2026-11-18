# Worker Sample: `navigator-playwright`

End-to-end sample and security harness for the optional **Navigator Playwright** browser-automation worker (`workers/navigator-playwright/Dockerfile`), covering specification **§5.9 (`WSE2E-IMP-018`, `WSE2E-AT-059..065`)**.

---

## 1. Canonical Production Image & Build Command

- **Canonical Dockerfile**: `workers/navigator-playwright/Dockerfile`
- **Canonical Image Name**: `vuminkosimatibe/openconnect-worker-navigator-playwright:e2e-local`
- **Model Volume / Provisioner**: **None.** This worker uses a pinned Node.js LTS runtime (`node:22.17.1-bookworm-slim`) and pinned `playwright-core@1.49.1` with Chromium pre-installed at `/ms-playwright`. No model volume (`oc_*_models`) or model provisioner service is used (`WSE2E-AT-059`).

Build the production image locally from the repository root:

```bash
docker build \
  -f workers/navigator-playwright/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-navigator-playwright:e2e-local \
  workers/navigator-playwright
```

---

## 2. Docker Compose Invocation

Start the sample stack (`synthetic-site` + `navigator-playwright` on the isolated `oc-test-private` network):

```bash
docker compose -f samples/workers/navigator-playwright/compose.e2e.yaml up -d --build
```

Run the shared harness across tiers:

```bash
# T0 static + T1 offline contract & Node security suite (no Docker required)
sh samples/workers/navigator-playwright/scripts/run-e2e.sh

# T2 container startup + readiness
TIER=t2 sh samples/workers/navigator-playwright/scripts/run-e2e.sh

# T3 runtime positive & negative security probes
TIER=t3 sh samples/workers/navigator-playwright/scripts/run-e2e.sh
```

---

## 3. Environment Variables, Secrets & Host Ports

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `PORT_NAVIGATOR_PLAYWRIGHT` | `8088` | Host port mapped to container port `8080`. |
| `NAVIGATOR_WORKER_TOKEN` | `e2e-test-token-do-not-use-in-prod` | Required Bearer token for `/internal/v1/*`. Empty/unset fails closed with `503 capability_unavailable`. |
| `NAVIGATOR_ALLOWED_ORIGINS` | `https://example.com,http://synthetic-site:8090` | Comma-separated exact-origin allowlist enforced prior to navigation/observation/capture. |
| `PLAYWRIGHT_LAUNCH` | `0` (`1` in image default) | Set to `0` for deterministic simulator in fast CI or `1` to launch headless Chromium per request. |
| `TMPDIR` | `/tmp/nav-tmp` | Ephemeral `tmpfs` mount (`noexec,nosuid,size=512m`) where per-run `nav-play-*` profile directories are created and deleted. |

---

## 4. Expected Capabilities & Allowlisted Tool Surface

- `GET /health/live` -> `200 {"status":"live"}`
- `GET /health/ready` -> `200 {"status":"ready","worker":"navigator-playwright","capabilities":["observe","find-target","navigate","click","fill","select","wait","capture"],"browser":"simulator"|"chromium"}`
- `GET /internal/v1/capabilities` (Bearer auth) -> `schemaVersion: "navigator.v1"`, allowlisted tools (`observe`, `find-target`, `navigate`, `click`, `fill`, `select`, `wait`, `capture`), and explicitly denied tools (`browser_evaluate`, `evaluate`, `eval`, `script`, `exec`, `run-js`, `file-read`, `file-write`, `download`, `upload`).
- `POST /internal/v1/execute` (Bearer auth) -> executes a single allowlisted tool inside an ephemeral profile directory (`withFreshProfile`) that is unconditionally removed on completion or crash.

---

## 5. Offline Test Procedure & Acceptance Coverage (`WSE2E-AT-059..065`)

Run the complete offline test suite without Docker or network access:

```bash
sh samples/workers/navigator-playwright/tests/t1_contract.sh
```

| Acceptance ID | Scenario | Fixture / Test |
|---|---|---|
| `WSE2E-AT-059` | Production Dockerfile non-root (`10001:10001`), pinned `playwright-core@1.49.1`, no model volume or provisioner | `tests/t0_static.sh`, `tests/e2e_navigator_suite.mjs` |
| `WSE2E-AT-060` | Allowlisted tool surface (`observe`, `find-target`, `navigate`, `click`, `fill`, `select`, `wait`) against synthetic multi-page fixture | `fixtures/allowlisted-flow.json`, `fixtures/site/*` |
| `WSE2E-AT-061` | Denies `browser_evaluate`/`eval`/`exec`/`file-read`/`file-write`, `file://`/`javascript:`/`data:`, unapproved origins, loopback/RFC1918/link-local metadata `169.254.169.254`, DNS rebinding, cross-origin redirects, unapproved targets, and secret fills | `fixtures/ssrf-and-allowlist-negatives.json` |
| `WSE2E-AT-062` | Screenshot `capture` denied without consent (`403 approval_required`); with consent scrubs PII text and strips PNG `tEXt`/`iTXt`/`zTXt`/`eXIf` metadata chunks | `fixtures/capture-consent-and-redaction.json` |
| `WSE2E-AT-063` | Concurrent tenant/session runs use distinct `nav-play-*` profile directories with zero shared cookies/storage | `fixtures/cross-tenant-isolation.json` |
| `WSE2E-AT-064` | Bounded `wait` (`<= 10000ms`) and mid-run browser crash cleanup removes temporary profile directories in `finally` | `fixtures/crash-timeout-cleanup.json` |
| `WSE2E-AT-065` | When worker is absent/disabled, browser automation fails closed with `503 capability_unavailable` while client-side read-only tour/highlighting remains functional | `fixtures/worker-absent-fallback.json` |

---

## 6. Failure-Injection Commands & Evidence Artifacts

Probe negative paths against a running container:

```bash
# 1. Missing Bearer token -> 401 unauthorized
curl -i -X POST http://127.0.0.1:8088/internal/v1/execute \
  -H 'Content-Type: application/json' \
  -d '{"tool":"observe","args":{"url":"https://example.com"}}'

# 2. Denied arbitrary script tool -> 403 tool_not_allowed
curl -i -X POST http://127.0.0.1:8088/internal/v1/execute \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer e2e-test-token-do-not-use-in-prod' \
  -d '{"tool":"browser_evaluate","args":{"expression":"document.cookie"}}'

# 3. Cloud metadata SSRF probe -> 403 invalid_request / approval_required
curl -i -X POST http://127.0.0.1:8088/internal/v1/execute \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer e2e-test-token-do-not-use-in-prod' \
  -d '{"tool":"navigate","args":{"url":"http://169.254.169.254/latest/meta-data/"}}'
```

Evidence artifacts emitted by `shared/scripts/run-sample.sh`:
- `artifacts/worker-e2e/<run-id>/navigator-playwright/<tier>/result.json`
- `artifacts/worker-e2e/<run-id>/navigator-playwright/<tier>/summary.md`
- `artifacts/worker-e2e/<run-id>/navigator-playwright/<tier>/logs/container.sanitized.log`

---

## 7. Safe Teardown & Production Differences

Always tear down without `-v` / `--volumes`:

```bash
docker compose -f samples/workers/navigator-playwright/compose.e2e.yaml down --remove-orphans
```

**Production differences**:
1. In production, `NAVIGATOR_WORKER_TOKEN` is injected from a secret store and rotated per deployment; `e2e-test-token-do-not-use-in-prod` is strictly for local testing.
2. In production, `PLAYWRIGHT_LAUNCH=1` launches headless Chromium inside a sandboxed container with seccomp/AppArmor profiles and egress firewalling to approved origins only.
