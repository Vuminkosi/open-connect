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

## Task 005D live ExternalGateway evidence

Current implementation uses the canonical root `docker-compose-all.yml` with `todo-web-app` and `gateway-host` as separate services. The gateway uses the configured Meta-compatible OpenAI cloud endpoint and model from environment configuration; credentials are not printed.

Command:

```powershell
./scripts/run-live-todo-e2e.ps1
```

Observed PASS:

- Gateway health: HTTP 200.
- Unauthenticated session creation: HTTP 401.
- Authenticated session creation: HTTP 201.
- Authenticated task acceptance: HTTP 202.
- Native MAF task completed: terminal state `completed`.
- Todo service mutation verified through the existing `InMemoryTodoStore` internal API.
- Live response produced `weighbridge` Todo matches.
- Cross-tenant internal Todo request: HTTP 401.
- Inference worker readiness: HTTP 200 in Compose logs.
- Root Compose UI: HTTP 200 on port 5000.

The first live attempt exposed a real contract defect: model-generated priority text was rejected by the Todo internal API because the request used an enum JSON shape. The API was corrected to accept and validate the string priority contract, then the live scenario passed.

This proves ExternalGateway mode and live Meta tool execution. SameHostGateway, interactive approval prompts, WebSocket replay, gateway restart recovery, live voice transcription and automated browser/axe tests remain separate gates.

## SameHostGateway verification

Optional `SameHostGateway` configuration was added to `samples/Blazor` with development-only claims, same-origin client header propagation, OpenConnect route mapping and the existing Meta Todo integration.

Observed in a local .NET process on port 5400:

- Blazor app startup: PASS.
- OpenConnect `/openconnect/v1/health`: PASS, HTTP 200.
- OpenConnect `/openconnect/v1/health/ready`: PASS, HTTP 200 with development auth.
- Same-host task acceptance: PASS, HTTP 202.
- Same-host task completion: FAIL; the task reached `failed` during the first live run. The process logs exposed only the generic `Host task execution failed` warning, so this remains a live integration defect to diagnose before claiming SameHostGateway completion.
- Same-host internal Todo API: PASS, HTTP 200.

The external Compose path remains the verified live path. The same-host source mode is compiled and starts, but its live MAF execution is not yet closed.

## SameHostGateway live execution update

After adding agent registration and configurable same-host base URL, the optional same-host process was run on `http://127.0.0.1:5400` with Development authentication, Meta configuration, and the existing Todo store.

Observed:

- Same-host startup and OpenConnect health: PASS.
- Same-host authenticated task acceptance: PASS, HTTP 202.
- Same-host native Meta task completion: PASS, terminal state `completed`.
- Same-host Todo store verification: PASS, weighbridge records present.

The first same-host run failed because the sample omitted `AddOpenConnectAgent("worker-sample-direct")`; that was corrected before the passing run.

## Voice update

The transcription worker is configured for Meta cloud routing and reports `cloudConfigured=true` and `cloudAvailable=true`. A synthetic WAV execution request with the worker token returned HTTP 503 `model_unavailable`; no transcript was claimed. The exact Meta cloud capability remains NOT VERIFIED and must not be presented as passing live transcription evidence.

## SwiftAidv2 transcription parity

SwiftAidv2 was inspected at `C:\DevOps\SwiftAidv2`. Its verified Meta upload contract is:

- `multipart/form-data`
- JSON settings in field `request`
- audio bytes in field `audio`
- filename `ellai-capture.wav`
- `audioEncoding: WAV`
- `mode` from `OpenConnect__ASRMode`
- `language` from `OpenConnect__Asr__Language`
- `model` from `OpenConnect__ASR_MODEL`
- Bearer API key from `OpenConnect__ApiKey`

OpenConnect transcription was changed to match that contract. The previous `file` plus individual form fields shape was rejected by Meta with HTTP 415.

After the parity fix, the live transcription request returned HTTP 200 with a final transcript envelope:

```text
transcript: "Yeah."
isFinal: true
durationMs: 1000
```

The configured Meta endpoint, model, mode, language and bearer credential were used without printing the credential. The worker response still labels its configured backend as `local-fake` due the existing worker backend enum/reporting path; the cloud-first route returned the successful provider transcript. That backend label remains a reporting defect to correct separately.

## SwiftAid-compatible realtime transcription

OpenConnect now proxies the SwiftAid-compatible Meta realtime protocol at `/internal/v1/realtime`:

- First text frame is the JSON handshake with Bearer access token, `PCM_24KHZ`, model and mode.
- Binary frames are validated PCM16 and forwarded to Meta.
- `endStream` is forwarded.
- Provider partial/final JSON events are relayed.
- Cloud privacy and worker authentication remain enforced.

Command:

```powershell
./scripts/run-live-realtime-transcription.ps1
```

Observed PASS:

```text
ACK sessionId=voyager/duplex/...
final transcript event: Yeah
```

The test uses synthetic zero PCM audio and proves protocol/transport/final-event handling; it is not a speech quality benchmark.
