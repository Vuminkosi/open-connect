# OpenConnect.LiveUI

OpenConnect.LiveUI provides the versioned UI manifest, typed action and receipt contracts, safe configuration, manifest hashing, and action orchestration used by LiveUI adapters.

## Register

The host calls AddOpenConnectLiveUI and configures the supported modes and safe defaults. Hosts provide an ILiveUIManifestProvider, an ILiveUIActionAuthorizer, and an ILiveUIRuntime. The authorizer receives the current route, target, manifest, and typed action. It must bind authorization to the authenticated host context and return UserConfirmed only when a matching user approval exists. No target capability grants permission by itself.

LiveUIOrchestrator rejects invalid or stale manifests, unknown routes and targets, unsupported target capabilities, unauthorized actions, duplicate action IDs within a session, and actions without verified postconditions. LiveUIManifestHasher computes the content digest over stable, sorted manifest content.

The core package has no browser, UI framework, model provider, or persistence dependency.

## Runtime metrics

The package publishes metrics through the `OpenConnect.LiveUI` meter. Duration instruments use milliseconds. Where tags are present, they contain only action type and outcome; metrics do not include session IDs, target IDs, UI labels, or user content.

- `openconnect.liveui.manifest.load.duration`: current manifest provider retrieval.
- `openconnect.liveui.target.resolution.duration`: browser resolver time for a target.
- `openconnect.liveui.target.resolution.failures`: ambiguous or unresolved target count.
- `openconnect.liveui.browser.action.duration`: browser action time after resolution.
- `openconnect.liveui.action.execution.duration`: orchestrator action time through the runtime receipt.
- `openconnect.liveui.confirmed.state.duration`: request-to-receipt time for verified `WaitForState` actions.
- `openconnect.liveui.speech.highlight.offset`: browser audio timeline segment start to verified cue highlight; records zero when the cue target is already active at the segment boundary.

Browser timing fields are accepted only when finite and within the runtime's 120-second maximum. Hosts can collect these instruments with `System.Diagnostics.Metrics` listeners or their OpenTelemetry meter provider. Speech offset measures until the Blazor start callback returns after the matched browser action receipt is verified. It does not measure acoustic output latency or word-level alignment.
