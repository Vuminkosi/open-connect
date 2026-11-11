# Task 005C evidence

**Current revision:** `afbd26c` at audit start; blocker documentation follows in a new commit.
**Disposition:** BLOCKED for complete same-host Todo gateway execution.

## PASS

- Route-level composition is safe: `MapOpenConnect()` is idempotent and uses `/openconnect/v1`, disjoint from Blazor routes. Existing hosting tests cover mapping idempotency.
- External-gateway behavior remains the current default.
- Todo UI CRUD remains direct against the existing process-local `InMemoryTodoStore`.
- Existing Todo HTTP smoke passes after correcting its stale UI marker.
- Full non-live solution regression was green before this documentation-only feasibility update: 1,437 total, 1,436 passed, 0 failed, 1 skipped.

## BLOCKED

A genuine same-host Todo task/tool flow cannot be completed from the current repository without new sample-owned contracts because:

- `samples/Blazor` has no authentication scheme or tenant resolution.
- `samples/Blazor` has no `IHostTaskAgentExecutionFactory`.
- `samples/Blazor` has no `IChatClient` or model provider.
- The existing worker gateway has no Todo store or Todo tools.
- `AddOpenConnectTool<T>` registers catalog metadata but does not itself create executable native MAF `AITool` instances.
- The client does not automatically forward the current Blazor circuit's authenticated cookies/bearer token or tenant headers.

Implementing a partial mode would either return task `503` or invent authorization/execution behavior, so it is not claimed.

## NOT VERIFIED

Same-host task acceptance, native MAF Todo tool mutation, approvals, cross-tenant Todo isolation, WebSocket replay, gateway restart recovery, optional-worker outage, live privacy/model egress, Playwright and axe-core browser tests.

## Canonical Compose live verification

The running root `docker-compose-all.yml` stack was inspected on the current machine. `todo-web-app` is built from `samples/Blazor/Dockerfile`; `gateway-host` is the separate OpenConnect gateway; `worker-inference` is configured from the Meta cloud environment variables.

Observed:

- `GET http://127.0.0.1:5000/` → HTTP 200.
- `GET http://127.0.0.1:8080/openconnect/v1/health` → HTTP 200.
- Inference worker `/health/ready` returned HTTP 200 in container logs.
- Authenticated session creation with synthetic development claims → HTTP 201.
- Unauthenticated session read → HTTP 401.
- Cross-tenant session read → HTTP 404.

This proves the existing ExternalGateway mode's UI startup, gateway health, inference readiness and route-level authentication/tenant isolation. It does not prove Todo tool execution because the separate gateway does not register the Blazor Todo store or Todo MAF tools.
