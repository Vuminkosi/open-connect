# Blazor Todo sample

The Blazor Server sample combines a Todo interface with MiniComposer and LiveUI. An interactive request stays in its existing OpenConnect session; the server can discover authorized UI capabilities, request a validated plan, execute actions on the connected page, and report verified receipts. The sample stores Todo items and OpenConnect state in its own SQLite database and applies its migrations at startup.

The sample supports a development-only `SameHostGateway` and an `ExternalGateway`. The external mode requires a gateway with authentication configured by its host. The sample's optional local service credential is restricted to Development and a loopback gateway; it is not a production user-identity mechanism.

## Configuration

Set configuration through environment variables or another ASP.NET Core configuration provider. Supply secrets through a secret store or environment, not source control.

| Setting | Purpose |
| --- | --- |
| `OpenConnect__Gateway__Mode` | `ExternalGateway` (default) or `SameHostGateway` (Development only). |
| `OpenConnect__DocumentDemo__Enabled` | Enable the document example only with Development `SameHostGateway`; defaults to disabled. |
| `OpenConnect__DocumentDemo__DocumentSocket`, `OpenConnect__DocumentDemo__ExtractionOrigin` | Protected document-worker socket and approved extraction-worker origin. |
| `OpenConnect__DocumentDemo__AllowInsecureHttp` | Explicitly permit HTTP to an isolated development extraction worker; defaults to false. |
| `GATEWAY_URL` | External gateway base URL when using `ExternalGateway`. |
| `OpenConnect__Gateway__SameHostBaseUrl` | Sample base URL used by the same-host client. |
| `OpenConnect__Gateway__LocalServiceBearer` | Optional 32-byte base64 local service credential for a Development loopback gateway. |
| `TODO_INTERNAL_TOKEN` | Required credential for the internal Todo API and its server-side caller. |
| `TODO_INTERNAL_TENANT` | Tenant allowed by the internal Todo API. |
| `OpenConnect__ApiUrl`, `OpenConnect__ApiKey`, `OpenConnect__ReasoningModel` | Model endpoint, credential, and model used by the same-host agent or sample gateway. |
| `OpenConnect__LiveUI__Inference__Worker__Endpoint`, `OpenConnect__LiveUI__Inference__Worker__Token` | Optional private worker endpoint and server-side credential. |
| `OpenConnect__LiveUI__SameHostGatewayPlanner` | Explicitly enable guarded gateway planning in Development same-host mode. With a worker configured, it is used before the gateway planner; without one, planning uses the gateway directly. |
| `OpenConnect__LiveUI__ExternalGatewayPlanner` | Opt in the ExternalGateway client to the gateway's guarded planner fallback. The gateway must also enable `OpenConnect__LiveUI__ExternalPlanner__Enabled`. Both settings default to disabled. |
| `OpenConnect__LiveUI__Inference__Gateway__FallbackPolicy` | Optional fallback policy: `Disabled`, `OnUnavailable` (default), or `OnFailure`. An external planner must still be explicitly enabled on both client and gateway. |
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

The `Invalid_real_worker_plan_falls_back_to_real_guarded_gateway` test in the worker test project requires both the real-provider settings and a configured loopback worker endpoint and token. It checks that `OnUnavailable` stops after an invalid worker plan, while `OnFailure` can use the privacy-guarded real planner. The connected `playwright.liveui-real-qwen-fallback.config.js` lane additionally requires `OPENCONNECT_LIVEUI_TEST_INVALID_PLAN=1`, `OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER`, and `OPENCONNECT_LIVEUI_WORKER_TOKEN`; it verifies a real worker rejection, gateway plan, UI actions, and receipts. These opt-in tests use a real model and may produce different results as model behavior changes.

The external-gateway fallback lane uses the same real-provider settings and runs with `npx playwright test --config=playwright.liveui-real-external-fallback.config.js` from `tests/Browser`. Its isolated gateway enables the external planner, authenticates the session, applies the external-model privacy gate, and validates the returned plan. A disposable loopback worker supplies the unavailability response. Set `OPENCONNECT_LIVEUI_TEST_INVALID_PLAN=1` to exercise a worker 422 response with the explicit `OnFailure` policy instead.

The combined real-provider and real-worker browser acceptance lane runs the specified Todo requests through the connected application and checks correlated action receipts. Set the real-provider settings above plus `OPENCONNECT_LIVEUI_REAL_WORKER_RUN=1`, `OPENCONNECT_LIVEUI_WORKER_ENDPOINT` (a loopback HTTP endpoint), `OPENCONNECT_LIVEUI_WORKER_TOKEN`, and `OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER` for a configured private worker. From `tests/Browser`, run `npx playwright test --config=playwright.liveui-real-gateway-worker.config.js`. This lane sends synthetic requests to the configured provider and may incur provider charges.

### Document example

The opt-in `/document-example` page runs an uploaded PDF, TIFF, PNG or JPEG
through the private document worker, then asks the configured extraction worker
for a `reference` candidate. It displays recognized pages and the candidate's
page-relative source citation for review. The example does not save the upload
or make business decisions. It requires the development same-host identity;
it is not a production authorization example. Use synthetic documents.

In the root Compose stack, enable the `documents` and `extraction` profiles,
provide their reviewed model configuration and worker credentials, and set
`OC_TODO_DOCUMENT_DEMO_ENABLED=true` and `OC_EXTRACTION_ENABLED=true`. Set
`OC_TODO_EXTRACTION_ALLOW_HTTP=true` only when the Compose network is an
appropriately protected development network; otherwise configure an HTTPS
origin. The default model configuration is intentionally unconfigured. The
example fails closed if authorization, privacy inspection, scanning, OCR or
extraction is unavailable; the configured privacy policy may block sensitive
OCR output. A candidate and its citation require human review.

### Knowledge example

The optional knowledge example indexes an already stored, operator-approved document
version and displays authorized hybrid-search passages with source locations.
Enable `OpenConnect__DocumentDemo__Enabled` and `OpenConnect__KnowledgeDemo__Enabled`
in the development-only same-host configuration. Configure
`OpenConnect__KnowledgeDemo__IndexOrigin`, `SearchOrigin`, and `EmbeddingOrigin`
for the respective private worker APIs. HTTPS is required unless
`OpenConnect__KnowledgeDemo__AllowInsecureHttp` is explicitly enabled for an isolated
development network. The existing worker token and signing-key configuration applies.

To use an entitled Cloud embedding deployment for the query embedding, set
`OpenConnect__KnowledgeDemo__UseCloudEmbedding=true`, point `EmbeddingOrigin` to the
Cloud gateway, and supply `OpenConnect__KnowledgeDemo__CloudBearerToken` as a
scoped machine credential. Indexing and search continue to use their private worker
credentials. The Cloud and index embedding spaces must match. In root Compose, the
corresponding variables are `OC_TODO_KNOWLEDGE_USE_CLOUD_EMBEDDING` and
`OC_TODO_KNOWLEDGE_CLOUD_BEARER_TOKEN`.

Set `OpenConnect__KnowledgeDemo__ObjectId`, `ObjectSha256`, `ObjectSize`,
`ObjectVersion`, and `EmbeddingSpaceId` to the authorized immutable object and the
matching provisioned embedding space. In root Compose, set both `OC_TODO_DOCUMENT_DEMO_ENABLED=true` and
`OC_TODO_KNOWLEDGE_DEMO_ENABLED=true`, then use the
corresponding `OC_TODO_KNOWLEDGE_OBJECT_ID`, `OBJECT_SHA256`, `OBJECT_SIZE`,
`OBJECT_VERSION`, and `EMBEDDING_SPACE_ID` variables with the full common
prefix. Configure the knowledge, reranker, and indexing profiles, and set
`OC_KNOWLEDGE_SEARCH_ENABLED=true` for the search service.
The default private service origins use HTTP and require explicit
`OC_TODO_KNOWLEDGE_ALLOW_HTTP=true` on a protected network. The object must
already exist in the private versioned bucket under the tenant's document storage contract. Configure retention,
malware scanning and access before enabling the example. The index worker scans and
verifies the object before processing; the example does not delete the source object.
Search results are cited passages, not generated answers. Model-backed workers and
the index service must be configured and ready independently.

### Optional worker capability monitoring

The development-only same-host sample can poll configured private workers and
publish their ready logical IDs in `/openconnect/v1/capabilities/ready`. Set
`OpenConnect__CapabilityMonitor__Enabled=true` and provide the worker token.
The root Compose configuration supplies the document and vision socket paths and
the private service origins; worker profiles and approved model configuration
must still be enabled separately. Missing or unready workers remain unavailable.

The Compose service origins use HTTP on the internal network, so they require
`OC_TODO_CAPABILITY_MONITOR_ALLOW_HTTP=true` only when that network is protected.
For HTTPS or alternate protected endpoints, use the corresponding
`OC_TODO_CAPABILITY_*_ENDPOINT` variables. Inference uses
`OPENCONNECT_INFERENCE_TOKEN` when configured; other sources use
`OPENCONNECT_WORKER_TOKEN`. Monitoring is disabled by default, and a readiness
advertisement does not authorize requests or prove execution.

### Optional Cloud privacy evidence

The sample can report content-free privacy decisions to the Cloud administration
service. Reporting is disabled by default. In root Compose, set
`OC_TODO_CLOUD_PRIVACY_ENABLED=true`, `OC_TODO_CLOUD_PRIVACY_ORIGIN` to the
HTTPS Cloud authority, and `OC_TODO_CLOUD_PRIVACY_CREDENTIAL` to an application
credential for the same tenant as the sample session. Provide a dedicated client
certificate with its private key in the application data volume and configure
`OC_TODO_CLOUD_PRIVACY_CERTIFICATE_PATH` and, if needed,
`OC_TODO_CLOUD_PRIVACY_CERTIFICATE_PASSWORD`. The default certificate path is
`/data/cloud-privacy-client.pfx`. Provision these secrets through the deployment's
secret-management process; the sample does not create them.

The Cloud service must trust the dedicated client certificate and the host must
trust the HTTPS authority. For the root Compose control plane, provision its
server certificate in the `oc_docs_tls` volume and configure `CLOUD_DOCS_URLS`
with both `http://+:8080` and an HTTPS listener, along with
`CLOUD_DOCS_TLS_CERTIFICATE_PATH` and, if needed,
`CLOUD_DOCS_TLS_CERTIFICATE_PASSWORD`. Enable `CLOUD_HOST_PRIVACY_TRUST_ENABLED`
and set `CLOUD_HOST_PRIVACY_CERTIFICATE_SHA256` to the dedicated host client
certificate fingerprint. The server certificate must match the authority hostname
and chain to a CA trusted by the sample host. For an explicitly pinned private
authority certificate, set `OC_TODO_CLOUD_PRIVACY_SERVER_SHA256` to its SHA-256
fingerprint; hostname, validity and server usage checks still apply. Rotate that
pin when the server certificate changes. Configure a migrated Cloud database
and tenant-bound application credential before enabling reporting.

If evidence cannot be acknowledged, the recording
firewall blocks the associated operation. Evidence contains action and category
counts, not document text or detected values. The configured detectors do not
guarantee detection of every sensitive value.
