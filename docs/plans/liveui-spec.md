# OpenConnect LiveUI Specification

## Scope and status

This document specifies the current .NET-first LiveUI architecture and its intended acceptance behavior. Implemented behavior is identified separately from planned or unverified behavior; requirements in this document are not evidence that a feature has shipped.

Primary assemblies are `OpenConnect.LiveUI`, `OpenConnect.LiveUI.Agents`, `OpenConnect.LiveUI.Blazor`, and `OpenConnect.LiveUI.Compiler`. `OpenConnect.LiveUI` owns framework-independent contracts and orchestration. Blazor owns browser interop and circuit context. Agent Framework integration exposes governed tools. The compiler analyzes Razor components and emits an embedded manifest.

## .NET integration

For a source-based integration in this repository, reference the LiveUI Blazor project and compiler as the Todo sample does. Register LiveUI services and a host authorization policy:

```csharp
builder.Services.AddOpenConnectLiveUIBlazor(options =>
{
    options.Discovery.Mode = LiveUIDiscoveryMode.CompiledFirst;
    options.Discovery.EnableRuntimeFallback = true;
    options.Interaction.EnableNavigation = true;
    options.Interaction.EnableFiltering = true;
    options.Interaction.EnableHighlighting = true;
    options.Narration.Enabled = true;
    options.Narration.SynchronizeWithSpeech = true;
    options.Security.RequireConfirmationForWrites = true;
});

builder.Services.AddScoped<ILiveUIActionAuthorizer, AppLiveUIActionAuthorizer>();
```

The default authorizer denies actions. A production authorizer must derive identity and permission from trusted host state, enforce tenant/session ownership, and require confirmation as policy demands. Never authorize from model-provided arguments alone.

Render `OpenConnectLiveUIProvider` around the existing application content and retain the existing OpenConnect provider/composer integration. The LiveUI provider loads the embedded manifest unless the application supplies one explicitly. MAF callers create tools from the scoped `LiveUIAgentTools`; tool context is resolved by the host and is not supplied by the model.

No released package installation/version is asserted here. Use the repository's package publishing process when consuming released packages; source project references are appropriate for in-repository development.

## Manifest and compiler

The manifest schema identifier is `openconnect.liveui/v1`. It contains a build identifier, a SHA-256 content hash, routes, and route targets. Targets have stable IDs, kind, label, capability set, optional semantic role/selector/parent, and optional confirmation requirement. The Razor compiler records parent IDs for controls nested within explicitly marked regions; the browser resolver scopes target matching through that parent chain. The manifest validator rejects missing or cyclic parent references. Routes and targets are sorted when hashing so the digest is deterministic for equivalent content.

`RazorLiveUIManifestCompiler` uses Razor's project engine and component intermediate representation to inspect `.razor` files. It discovers statically declared routes and supported controls, follows statically referenced component relationships where available, and emits diagnostics for invalid or duplicate targets. The current scanner also examines markup associated with discovered components; it is not a complete semantic evaluator for arbitrary C# expressions or every third-party component.

The Todo project invokes the compiler before resource naming and embeds the generated manifest. `LiveUICompiledManifestLoader` loads that resource. Static compilation cannot guarantee that a component renders a unique or visible DOM target at runtime; resolution must still verify the browser result.

## Action and receipt contract

`LiveUIAction` binds action ID, schema version, session ID, manifest hash, route ID, optional target ID, action type, typed parameters, and timeout. Supported action enum values are Navigate, Open, Close, Click, Focus, Scroll, Highlight, ClearHighlight, SetValue, Select, SelectTab, Inspect, and WaitForState.

`LiveUIActionReceipt` reports the action/session, outcome, stable code, completion time, whether the postcondition was verified, and optional verified state. Outcomes distinguish Completed, Denied, Ambiguous, MissingTarget, Uncertain, Failed, and Conflict. Only verified postconditions may produce Completed.

The orchestrator validates manifest version and hash, route/target membership, action capability, options, duplicate action IDs, authorization, and runtime result. The browser runtime rejects ambiguous resolution and applies bounded interactions; it does not run model-generated JavaScript.

## Agent-to-Blazor request path

The MAF adapter exposes `liveui_get_current_page`, `liveui_navigate`, `liveui_inspect`, `liveui_execute`, `liveui_await_state`, `liveui_highlight`, and `liveui_clear_highlight`. Each operation emits a content-bounded `liveui.request` event into the bound session and awaits a correlated response through `IClientInteractionBroker`. The Blazor `OpenConnectLiveUIProvider` observes the composer session event, runs the operation through scoped `LiveUIAgentTools`, and posts a response using the .NET client. The gateway checks authenticated ownership, payload bounds, a matching pending correlation, and result kind before appending the response event. The agent tool returns only the correlated result to MAF.

Timeout and caller cancellation bound the wait. Caller cancellation emits a correlated `liveui.cancel` session event; the composer session cancels the matching Blazor operation, the provider clears LiveUI highlights once for that cancellation correlation, and any stale response is suppressed. Highlight cleanup also runs when cancellation arrives after the browser action receipt. The browser response path is asynchronous and depends on the connected composer session and provider. A disconnected client cannot complete a browser action; the tool must time out or cancel and must not claim success.

## Authorization and privacy boundaries

The model chooses among tool operations, authorized targets, and the session's authorized route catalog. If the current route cannot be inspected, the tool returns no targets for it but may still return routes the host permits the session to navigate to. It cannot choose the session, principal, tenant, current route, manifest hash, or arbitrary script. Those values come from trusted .NET context. The host authorizer remains the authority for every action.

Current sample policy allows only the explicitly listed Todo read/navigation/filter/highlight behaviors. It does not grant general-purpose CRUD. Sensitive input types are excluded from compiler discovery and the runtime denies password/sensitive value entry. UI labels and inspected text remain untrusted content.

The in-memory session store currently backs the interaction broker registration. This provides process-local correlation; it is not durable across process restart and is not a distributed broker. A multi-instance deployment requires a shared, durable session/event implementation before relying on cross-instance browser acknowledgements.

## Browser behavior

The Blazor runtime resolves compiled descriptors using known selectors, optional runtime IDs, and browser semantic queries, scoped through the compiled parent chain where available. It rejects multiple matches within that scope. It supports the action subset implemented by the adapter, including focus, scroll, highlight, clearing highlights, value entry, selection, tab activation, inspection, route navigation, and bounded state waits.

Compiled lists, tables, and explicitly marked regions advertise `WaitForState` so a host policy can permit verification after a supported control changes existing application state. The Todo sample allows bounded `textContains` and `textNotContains` checks on its task list and marked `todo-list` region. The stable region remains rendered when the list is replaced by the empty-state message, allowing that result to be verified after Blazor rerenders.

Navigation is restricted to manifest routes and is complete only after route readiness is observed. Highlighting is non-modal, does not intentionally move focus, and respects reduced motion. A .NET agent may request a temporary highlight for up to 30 seconds; the Blazor runtime replaces an earlier timer when the same target is highlighted again, and explicit clear or cancellation removes the highlight. When `Narration.SynchronizeWithSpeech` is enabled, the agent tools suppress temporary expiry so playback lifecycle events control cleanup. Traditional Navigator tour behavior remains separate.

The current runtime does not establish complete support for every MudBlazor or arbitrary custom component pattern. Unsupported, stale, hidden, or ambiguous targets must fail visibly. In the Todo flow, search verification uses the stable marked region: the list disappears in the empty state, and the broad `.mud-list` selector can become ambiguous when another visible component renders a matching list. Native component adapters may be added where browser semantics alone cannot verify application state.

## Speech and timeline

Speech remains on the existing composer/TTS path. `MiniComposer` publishes actual audio-player lifecycle events through its cascading `ComposerSession`. `OpenConnectLiveUIProvider` subscribes to those events and uses the playback-epoch fence to ignore stale completions and interruptions before clearing highlights; current response completion or interruption clears them, including the no-speech response-completed path. Hosts do not need to wire a second callback for LiveUI cleanup. When synchronization is enabled, `liveui_highlight` accepts a bounded zero-based UTF-16 response range, and the provider switches among authorized target IDs when playback starts an overlapping audio segment. This is segment-level timing; the agent supplies the target-to-range association, so semantic alignment is not independently verified. The `openconnect.liveui.speech.highlight.offset` histogram records milliseconds from the browser audio timeline's scheduled segment start until Blazor completes the start callback after verifying the matched highlight receipt; it records zero when the same cue target is already highlighted at the boundary. It does not measure acoustic output latency or word-level alignment.

When speech is disabled or unavailable, UI action execution should remain functional. Disconnect, cancellation, and playback epoch changes must clear transient highlighting and prevent stale narration from being treated as current.

## Verification and performance

Current evidence includes compiler-generated Todo manifest browser checks, browser checks for existing tabs/search/list-state and empty-state verification/combined filtering, parent-scoped resolution in the presence of a duplicate outside the region, popup-free highlighting, composer audio lifecycle, and filtering with voice output disabled (10/10 passed in Development on 2026-11-14). These browser checks invoke the bundled LiveUI runtime against the actual sample DOM; the sample's configured external gateway was unreachable, so they do not prove the MAF request/response path. The Blazor provider bridge and cancellation suite passed 9/9 on .NET 8, 9, and 10, including bounded temporary-highlight duration propagation and cancellation cleanup. The MAF-to-session-broker highlight round trip passed 2/2 on each of .NET 8, 9, and 10; the correlated cue offsets are now asserted after event serialization. The Blazor browser runtime navigation tests passed 2/2 on each of .NET 8, 9, and 10, covering route handoff/readiness and route mismatch. Focused compiler tests passed 4/4 and manifest/orchestrator tests 13/13 on .NET 8, 9, and 10; agent tests passed 10/10 on .NET 8, 9, and 10. The sample Debug and Release builds passed with warnings treated as errors after these changes. The hosting unit suite passed 153/153 before the recent empty-state work. Real model-provider acceptance for all six user journeys is not verified; no provider endpoint or credentials were configured in the current environment.

The playback-fence unit tests passed 3/3 on .NET 8, 9, and 10, and the Blazor Todo sample Release build passed with warnings as errors. These checks cover stale playback epochs and current-response cleanup, not narration-range to target alignment.

No latency or synchronization benchmark is claimed. The SDK emits metrics for manifest provider load, browser target resolution latency and failures, browser action duration, total action execution, verified-state latency, and the segment-start-to-verified-highlight synchronization offset. A focused Playwright browser fixture loading the actual Blazor runtime source passed 2/2 highlight lifecycle checks: independent temporary expiry, explicit clear, and replacing a target's earlier timer. This fixture does not establish sample-host, provider, or speech synchronization acceptance. Benchmark results still require representative applications and browser conditions.

Additional playback-lifecycle evidence: the focused `LiveUIProviderClientInteractionTests` command passed 12/12 on each target (`-f net8.0`, `-f net9.0`, and `-f net10.0`). It covers two cue ranges switching targets at actual composer audio-segment start events, host-free response-completion cleanup, and malformed cue rejection. `LiveUIAgentToolsTests` passed 17/17 on each target, including connected-tool cue propagation and range bounds. The Todo sample Release build passed with warnings as errors (0 warnings, 0 errors). An unfiltered .NET 8 run of `OpenConnect.Blazor.Tests` currently fails 11 tests during synchronous bUnit disposal because `MudBlazor.PointerEventsNoneService` requires async disposal; the focused LiveUI provider tests pass.

Synchronization-offset instrumentation evidence: the focused provider suite passes 12/12 on .NET 8, 9, and 10 with a `MeterListener` asserting two measured target transitions and no measurement for an unmatched cue segment. The actual browser audio helper test passes 1/1 against the current Todo sample, verifying its PCM playback callback reports a nonnegative audio-timeline offset to .NET. This exercises generated PCM, not model-generated TTS or a real model-provider turn; no production latency benchmark is claimed.

Current-checkout browser acceptance: the Todo Blazor sample was started in Development on an isolated localhost port with deterministic acceptance data and a temporary SQLite database. `npm exec playwright -- test specs/liveui-todo-compiled-manifest.spec.js --reporter=list --output=test-results-liveui-local` passed 9/9 in Chromium. This exercised the running Blazor sample and its bundled LiveUI browser runtime for the existing Completed tab, search, scoped target resolution, rerender and empty-state verification, combined filtering, retained UI state, and popup-free highlighting. The test invokes the browser runtime directly; it does not traverse the MAF/session broker path or call a real model provider. The pre-existing service on port 5000 did not serve the current LiveUI static asset, so acceptance was rerun against the isolated current-checkout sample rather than that service.

The Blazor adapter caches validated embedded manifests by application assembly name, module version ID, and embedded resource name. This scopes cache entries to the compiled application build; invalid resources are not returned as manifests.

## Extension points

Other .NET UI frameworks can implement `ILiveUIRuntime`, `ILiveUIManifestProvider`, and `ILiveUIAgentContextAccessor` while reusing the shared action contracts, options, orchestrator, and MAF tools. A future TypeScript/browser package may share the versioned manifest and action schema, but no independent JavaScript-first SDK is required for the current .NET-first delivery.
