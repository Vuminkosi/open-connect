# Blazor Todo sample

The Blazor Server sample combines a Todo interface with MiniComposer and LiveUI. An interactive request stays in its existing OpenConnect session; the server can discover authorized UI capabilities, request a validated plan, execute actions on the connected page, and report verified receipts. The Todo list uses an in-memory store. The sample's EF Core infrastructure uses its own SQLite database and applies its migrations at startup.

The sample supports a development-only `SameHostGateway` and an `ExternalGateway`. The external mode requires a gateway with authentication configured by its host. The sample's optional local service credential is restricted to Development and a loopback gateway; it is not a production user-identity mechanism.

## Configuration

Set configuration through environment variables or another ASP.NET Core configuration provider. Supply secrets through a secret store or environment, not source control.

| Setting | Purpose |
| --- | --- |
| `OpenConnect__Gateway__Mode` | `ExternalGateway` (default) or `SameHostGateway` (Development only). |
| `GATEWAY_URL` | External gateway base URL when using `ExternalGateway`. |
| `OpenConnect__Gateway__SameHostBaseUrl` | Sample base URL used by the same-host client. |
| `OpenConnect__Gateway__LocalServiceBearer` | Optional 32-byte base64 local service credential for a Development loopback gateway. |
| `TODO_INTERNAL_TOKEN` | Required credential for the internal Todo API and its server-side caller. |
| `TODO_INTERNAL_TENANT` | Tenant allowed by the internal Todo API. |
| `OpenConnect__ApiUrl`, `OpenConnect__ApiKey`, `OpenConnect__ReasoningModel` | Model endpoint, credential, and model used by the same-host agent or sample gateway. |
| `OpenConnect__LiveUI__Inference__Worker__Endpoint`, `OpenConnect__LiveUI__Inference__Worker__Token` | Optional private worker endpoint and server-side credential. |
| `OpenConnect__LiveUI__SameHostGatewayPlanner` | Explicitly enable guarded gateway planning in Development same-host mode. With a worker configured, it is used before the gateway planner; without one, planning uses the gateway directly. |
| `OPENCONNECT_BLAZOR_SQLITE_PATH` | Optional SQLite database path. |

For same-host development, configure the model settings, Todo credential, gateway mode, and sample base URL, then run:

```powershell
dotnet run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj
```

For an isolated external-gateway development setup, configure matching `OC_LOCAL_SERVICE_AUTH_ENABLED`, `OC_LOCAL_SERVICE_AUTH_SECRET`, `OC_LOCAL_SERVICE_AUTH_TENANT_ID`, and `OC_LOCAL_SERVICE_AUTH_PRINCIPAL_ID` on the gateway, and supply the secret as `OpenConnect__Gateway__LocalServiceBearer` to the sample. The gateway accepts this credential only on loopback in Development. Production deployments need their own authenticated identity and credential propagation.

The optional worker is described in [its README](../../workers/liveui-inference/README.md). Worker plans are proposals: the application still authorizes each action and checks its receipt. If no permitted planner is available, the server agent can use the authorized page manifest and governed tools to continue.

## Verification

Run `dotnet build samples/Blazor/OpenConnect.Samples.Blazor.csproj -c Release` to build the sample. The connected browser suite is `npm run test:liveui-connected` from `tests/Browser`; it uses scripted inference. The opt-in real-worker lane requires a configured private worker and must be reported separately from scripted tests.

The gateway planner's real-provider acceptance tests require `OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN=1` and the `OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT`, `OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY`, and `OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL` settings. Run `dotnet test samples/workers/shared/OpenConnect.Samples.Workers.TestKit/OpenConnect.Samples.Workers.TestKit.csproj -c Release --filter FullyQualifiedName~LiveGatewayPlannerAcceptanceTests`. These tests send synthetic requests to the configured provider and verify proposed plans; they do not execute the plans in a browser.

The opt-in `Unavailable_worker_falls_back_to_real_firewall_guarded_gateway_planner` test in the same project checks a worker HTTP 503 followed by the configured real provider. It verifies the plan and confirms the external-model request passed through the privacy gate; the worker response is supplied by a disposable loopback server.

The opt-in connected fallback lane requires the real-provider settings above and `OPENCONNECT_LIVEUI_REAL_GATEWAY_PLANNER_RUN=1`. From `tests/Browser`, run `npx playwright test --config=playwright.liveui-real-fallback.config.js`. A disposable loopback worker reports unavailable after readiness; the test verifies the real guarded planner, the Todo action, and its receipt.

The combined real-provider and real-worker browser acceptance lane runs the specified Todo requests through the connected application and checks correlated action receipts. Set the real-provider settings above plus `OPENCONNECT_LIVEUI_REAL_WORKER_RUN=1`, `OPENCONNECT_LIVEUI_WORKER_ENDPOINT` (a loopback HTTP endpoint), `OPENCONNECT_LIVEUI_WORKER_TOKEN`, and `OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER` for a configured private worker. From `tests/Browser`, run `npx playwright test --config=playwright.liveui-real-gateway-worker.config.js`. This lane sends synthetic requests to the configured provider and may incur provider charges.
