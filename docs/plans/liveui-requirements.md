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

The repository currently contains .NET manifest/action contracts and orchestration, a Razor compiler, a Blazor browser runtime, MAF tools, and a correlated agent-to-Blazor request/response path. The Blazor Todo sample exercises existing tabs and search controls. The in-process integration test covers an agent tool request through an authorized browser action and verified gateway receipt. Scenario D's browser runtime now highlights a unique visible list row selected by bounded `matchText` and rejects ambiguous matches without a side effect. The sample prompt maps Todo priority enum values explicitly and requires clarification when the highest incomplete priority is tied or absent.

That evidence does not establish every journey above. Scenario D has browser and deterministic tool-contract evidence, but model-driven recommendation acceptance is NOT VERIFIED. Scenario E now has a deterministic same-host MAF-to-browser integration run; real model-provider output and successful TTS playback remain NOT VERIFIED. Scenario F through a real model provider, exhaustive authorization and tenant matrices, performance measurements, multi-item highlight timelines, and complete semantic speech synchronization remain separate acceptance work. See [the implementation specification](liveui-spec.md) for current boundaries and evidence labels.
