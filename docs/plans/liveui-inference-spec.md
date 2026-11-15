# LiveUI inference specification

## Request and plan

The .NET contract consists of an intent, trusted session and route identifiers, a LiveUI schema version, and the route's allowed target/action capability pairs. A plan contains that schema version and a list of typed action names, target IDs, and an optional bounded value. Current planner actions are `activate`, `navigate`, `setValue`, `select`, `highlight`, `focus`, and `scroll`.

The validator rejects a schema mismatch, an empty or oversized action list, unknown targets, actions absent from the target capability set, unexpected values, and values over 256 characters. The router requires a valid plan before returning it. `LiveUIInferenceActionMapper` converts validated proposals into the existing typed `LiveUIAction` contract with the trusted manifest and request session. The resulting actions still require existing authorization and runtime execution.

## Routing

The default is `BrowserFirst`, with browser inference enabled and the `OnUnavailable` fallback policy selected. The optional worker is disabled until the host registers it and enables `Worker.Enabled`. A guarded server inference provider is used only when the host explicitly registers one; the default `Gateway.Enabled` option alone does not create a provider or cause an LLM call. When a browser model is already available, the Blazor provider initializes it automatically when a plan is requested. A downloadable model is never downloaded automatically. `BrowserFirst` and `Automatic` prefer the browser provider. `BrowserOnly` never attempts worker or gateway inference. `GatewayFirst` and `GatewayOnly` require an explicitly enabled and registered gateway provider. `OnUnavailable` permits fallback when a provider is unavailable; `OnFailure` also permits fallback after provider or plan failure. Caller cancellation is propagated without fallback. The default planning timeout is 35 seconds, bounded by validation to at most two minutes, so it can accommodate the optional worker's 30-second default request limit.

The gateway contract is host-owned because only the host has the authenticated identity and egress policy needed to enforce safe remote inference. A gateway implementation must not infer that browser failure authorizes sending client UI context remotely.

## Metrics

Inference routing publishes the `OpenConnect.LiveUI.Inference` meter:

| Instrument | Type and unit | Meaning |
| --- | --- | --- |
| `openconnect.liveui.inference.requests` | Counter | Completed, unavailable, invalid-plan, cancelled, timed-out, or failed planning requests. |
| `openconnect.liveui.inference.duration` | Histogram, milliseconds | Total provider routing and planning duration. |
| `openconnect.liveui.inference.failures` | Counter | Requests ending with a safe error classification. Caller cancellations are not failures. |
| `openconnect.liveui.inference.fallbacks` | Counter | Attempts to use a provider after the preferred provider. |
| `openconnect.liveui.inference.invalid_plans` | Counter | Provider responses rejected by plan validation. |
| `openconnect.liveui.inference.model_availability` | Counter | Availability checks by provider and availability state. |

Metric attributes use bounded provider names, outcome/error classifications, and availability states. They never include prompts, DOM content, session or request identifiers, target IDs, labels, or user-entered values. Hosts can collect the meter through their existing .NET metrics or OpenTelemetry configuration.

## Blazor Prompt API lifecycle

`BrowserPromptApiInferenceProvider.GetAvailabilityAsync` checks secure context and browser API availability without creating a model session. `InitializeFromUserGestureAsync` is the explicit model creation entry point. The JS module checks availability, creates a browser-managed session, relays download progress, generates constrained JSON, aborts active generation when asked, and destroys the session on disposal or page shutdown. Session initialization is scoped to the Blazor provider instance.

`LanguageModel` is feature-detected, and no model brand or hardware profile is assumed. Deployment policy, secure context, browser version, hardware, and browser-managed model downloads can prevent availability. Unsupported environments return a safe unavailable result for routing.

## Verification boundary

Unit tests cover capability validation and routing policy. JavaScript Prompt API behavior must have deterministic mocked tests, and actual local inference must be reported separately for supported Chrome and Edge environments. No test using a mocked Prompt API proves actual local model compatibility.

The opt-in real-browser check is `npm run test:liveui-real-browser` from `tests/Browser`. It uses an installed Chrome or Edge channel selected by `LIVEUI_BROWSER_CHANNEL` (default `msedge`), a fresh Playwright profile, the connected Blazor Server session bridge, and the local acceptance chat fixture. It does not call a hosted model. If the browser model is not already available, the check skips without downloading it. To permit the browser-managed model download during this explicit acceptance run, set `OPENCONNECT_LIVEUI_ALLOW_BROWSER_MODEL_DOWNLOAD=1`; the test then clicks the app's model-setup control and waits for readiness. A passing run with a real available model is required before claiming browser/model compatibility. A skipped run is `NOT VERIFIED`.
