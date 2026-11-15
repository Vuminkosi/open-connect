# OpenConnect LiveUI Requirements

## Purpose

OpenConnect LiveUI lets an AI agent operate and explain a host application's existing interface through authorized, typed actions. The .NET SDK is the primary integration surface. Blazor is the first supported UI adapter and `samples/Blazor` is the acceptance consumer.

The application remains authoritative for its state and business rules. A model may propose an action, but the .NET host binds the session and context, applies authorization, invokes the browser runtime, and reports only observed outcomes.

## Product requirements

1. **Existing controls remain in use.** Supported navigation, tabs, search fields, lists, and highlights must work without replacing controls with OpenConnect components or requiring per-element registration. Optional component adapters may extend support.
2. **.NET-first SDK.** Public contracts, options, agent tools, authorization, and Blazor hosting are implemented in .NET packages. Browser code is a bounded implementation detail of the Blazor adapter.
3. **Compiled discovery first.** Razor-aware build analysis produces a deterministic, versioned manifest. Runtime discovery may supplement it when enabled; it must not turn ambiguous matches into arbitrary choices.
4. **Typed, verified interaction.** Actions carry session, schema, manifest, route, target, parameters, and timeout context. Dependent actions wait for a receipt. A dispatched event alone is not success.
5. **Host-owned authorization.** Every operation is authorized in the .NET host. Capabilities describe what a target can support and do not grant permission. The default Blazor authorizer denies actions.
6. **Privacy and safe failure.** Do not transmit full DOM snapshots by default, expose sensitive input values, execute model-generated scripts, or continue after stale context, authorization failure, or ambiguous target resolution.
7. **Speech reuses OpenConnect.** Narration uses the existing composer and speech lifecycle. UI actions remain available when speech is disabled or unavailable; playback synchronization must use actual playback events.
8. **Framework extension.** The core orchestration contracts remain independent of Blazor so other .NET UI adapters can provide a runtime, manifest provider, and context accessor.

## Initial user journeys

| Journey | Acceptance behavior |
|---|---|
| Open Todo list | Resolve the compiled Todo route, navigate through supported router semantics, verify readiness, and highlight the task region. |
| Show completed tasks | Activate the existing Completed control, wait for rerender, and verify its selected state. |
| Find matching tasks | Set the existing search control's value, wait for the application update, and verify the resulting list. |
| Highlight a task | Use verified application state; if the requested criterion is not grounded, ask for clarification. |
| Explain the screen | Describe only inspected, verified state and use popup-free highlighting. |
| Combined filters | Apply existing status and search controls, verify both and the resulting list, then explain the observed result. |

Filtering must preserve application state and use the application's existing UI or an explicitly trusted native adapter. LiveUI must not synthesize a filtered display that diverges from authoritative application state.

## Security and privacy requirements

- Bind each action and response to the authenticated tenant, principal, session, current route, and manifest version held by the host.
- Validate action shape, bounds, target capability, freshness, and postcondition before returning success.
- Reject cross-session, cross-tenant, replayed, stale, malformed, and unauthorized requests.
- Treat labels, page text, and inspected content as untrusted data, never as instructions.
- Do not include sensitive values in receipts, telemetry, or model context.
- Require host policy and, where configured, explicit user confirmation for consequential writes.
- On disconnect, cancellation, timeout, or unverifiable state, return a bounded failure/uncertain result and do not claim completion.

## Compatibility requirements

- Follow the repository's .NET target framework and central package/lockfile conventions.
- Preserve the existing MiniComposer, Navigator tours, application controls, and routing behavior.
- Keep popup-free LiveUI highlighting separate from traditional Navigator tour presentation.
- Keep schema changes versioned and reject unsupported manifest versions safely.

## Current implementation evidence and remaining acceptance

The repository currently contains .NET manifest/action contracts and orchestration, a Razor compiler, a Blazor browser runtime, MAF tools, and a correlated agent-to-Blazor request/response path. The Blazor Todo sample exercises existing tabs and search controls. The in-process integration test covers an agent tool request through an authorized browser action and verified gateway receipt. Scenario D's browser runtime now highlights a unique visible list row selected by bounded `matchText` and rejects ambiguous matches without a side effect. The sample prompt maps Todo priority enum values explicitly and requires clarification when the highest incomplete priority is tied or absent. The sample action authorizer requires exact compiled target IDs and kinds; .NET agent-tools and connected browser tests verify that task creation is hidden from page discovery and denied before browser execution, leaving the rendered task list unchanged.

The repeatable connected browser suite passes all six journeys A through F against the Blazor sample through its same-host gateway, MAF tool loop, interaction broker, LiveUI provider, and browser runtime. An earlier scripted-provider run passed 8/8 user-facing Chromium cases, including voice enabled and disabled and a policy-denied Add Task activation; the separate Blazor runtime performance probe passed 1/1. Scenario A starts on the compiled `/workspace-guide` route, discovers that its targets are not inspectable while the Todo home route is permitted, navigates to `/`, verifies route readiness, and highlights the existing task region. Scenario D verifies that `todo.list` returns the unique highest-priority incomplete item, confirms its expected Urgent priority and exact title, and only then permits the row highlight; Scenario E verifies inspection and popup-free highlighting. The opt-in real-provider Playwright suite in `tests/Browser` now passes all six journeys A through F (6/6 Chromium) against an isolated same-host Blazor application with synthetic Todo data and speech output disabled. It validates navigation, tabs, search, priority grounding and row highlighting, explicit screen explanation and task-area highlighting, and combined filters. Each run uses a fresh SQLite database and Data Protection key directory, and the suite observed no Todo writes. The earlier separate live-provider playback run described three tasks from a gateway Todo service while its rendered acceptance page showed a different four-task seed; that mismatch is confined to that earlier setup and does not apply to the isolated six-journey suite. The hosting response endpoint has focused tenant/principal ownership, cross-session correlation, cancelled-request, conflicting-replay, and 32-way concurrent idempotency checks (8/8 on .NET 8, 9, and 10); validation and append are atomic under the per-session journal lock. The core orchestrator has allow and deny tests for every LiveUI action type on .NET 8, 9, and 10, and tenant-scoped denial is now tested for each action type and every typed agent tool using session-to-tenant ownership checks. Authorization through a real external-provider gateway remains incomplete. The sample-specific exact-target policy passed the Blazor suite at 56/56 on .NET 8, 9, and 10, and the connected scripted-provider browser suite passed 9/9 after that policy change. A local browser-runtime microbenchmark now records 1,000 verified inspections on the Blazor Todo page, but comprehensive gateway/provider latency, failure-rate, timeline latency and semantic speech-synchronization measurements remain separate acceptance work; overlapping multi-target cues and fail-closed cleanup of partial highlights are now covered by the Blazor provider suite. See [the implementation specification](liveui-spec.md) for current boundaries and evidence labels.

Sensitive-input discovery was tightened in the Razor compiler to exclude password, email, telephone, numeric, hidden, and file input types; sensitive labels and metadata; payment-card autocomplete tokens; and dynamic input types that cannot be classified at build time. The Blazor runtime independently denies value entry and selection for sensitive fields using input type, autocomplete, accessible labels, placeholders, names, IDs, and associated labels. Verification on the current checkout: compiler tests passed 5/5 on .NET 8, 9, and 10; the Blazor sample Debug build passed on .NET 10 with zero warnings and errors; and the Chromium browser regression passed 1/1 against the running sample on an isolated local port, confirming denied value entry and selection left field values unchanged. The full Release solution build then passed with warnings treated as errors (0 warnings, 0 errors); the full solution test command passed 1,495 tests with one skipped test, and Blazor tests passed 51/51 on .NET 8, 9, and 10. Broader assistive-technology coverage of sensitive-label resolution remains unverified.

The 2026-11-15 verification supersedes older connected-suite counts: the isolated .NET Blazor connected suite passed 10/10 in Chromium, including all six user journeys, speech-enabled and speech-disabled filtering, policy denial, and voice-unavailable fallback. Its model provider is scripted, and speech synthesis deliberately returns HTTP 503; real-provider audio playback and word-level speech/highlight alignment remain NOT VERIFIED. The Blazor package consumer generated and loaded a manifest on .NET 8, 9, and 10 after packaging the 22 packable libraries. The rebuilt root Compose services passed app and gateway HTTP checks, and four seed-independent browser checks passed against the running Todo service. The full acceptance spec was stopped on the Compose instance after finding its normal sample data differs from the deterministic acceptance seed; no Todo items were created or deleted. See [the implementation specification](liveui-spec.md) for command-level results and remaining limitations.

The same 10/10 connected Chromium suite also passed against the rebuilt `todo-web-app` Docker image using an isolated container, temporary `/tmp` storage, and the local scripted provider. This verified the packaged Blazor application across all six journeys, speech enabled and disabled, authorization denial, TTS fallback, and the browser runtime measurement. The acceptance container and fixture were stopped after the run; the persistent Compose Todo page and gateway health endpoint still returned HTTP 200, and the Compose Todo data volume remained mounted.

The Blazor interaction provider now binds each browser action to the originating composer session and cancels in-flight work on session replacement. Its focused interaction suite passed 17/17, and the full Blazor suite passed 59/59 on each of .NET 8, 9, and 10. This verifies the default Blazor session binding and stale-receipt suppression. Later orchestrator and agent-tool tests cover cross-tenant denial for all action types and typed operations; authorization through a real external-provider gateway remains unverified.

After the session-binding change, the connected Chromium suite was rerun against a freshly rebuilt `todo-web-app` image and passed 10/10. The temporary acceptance container used a tmpfs database; after it was removed, the persistent Compose Todo and gateway endpoints both returned HTTP 200 and the named Todo volume remained mounted.

## Latest verification (2026-11-15)

The full connected Playwright suite passed 20/20 Chromium tests against the Blazor Todo sample. Coverage included the scripted browser Prompt API path, six responsive viewport sizes, the runtime benchmark, all six Todo journeys, speech enabled and disabled, task-row highlight transitions during narration, authorization denial, and fallback when TTS is unavailable. The browser-model test replaces `LanguageModel` with a test implementation; it does not verify Chrome or Edge built-in model inference.

The Release build of `OpenConnect.LiveUI.Blazor` passed for .NET 8, 9, and 10 with warnings treated as errors (zero warnings and errors for each target). Blazor integration tests passed 60/60 for each target; LiveUI inference tests passed 38 with one opt-in worker test skipped for each target; compiler tests passed 5/5, orchestrator tests 17/17, and agent-tool tests 25/25 for each target. Tenant-scoped denial across all orchestrator actions and typed agent tools is included in these latest counts.

The optional worker reported ready, its cached model matched the configured SHA-256, and an authenticated request to its plan endpoint returned HTTP 200 with a structured `qwen3-0.6b` action plan. This verifies the worker endpoint and model response; the opt-in .NET worker-client test was skipped in this test invocation. A real Chrome or Edge built-in model run remains NOT VERIFIED because the browser-managed model's storage prerequisite was not met. No model download was started.
