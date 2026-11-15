# OpenConnect.LiveUI.Blazor

The Blazor adapter supplies a scoped manifest state, browser action runtime, and the popup-free DOM interaction module. It composes with the application's existing controls and does not require replacing or annotating every control.

## Register and bootstrap

Call `AddOpenConnectLiveUIBlazor` during service registration and configure navigation, filtering, highlighting, and narration as required. Register a host `ILiveUIActionAuthorizer`; the default policy denies actions.

```csharp
builder.Services.AddOpenConnectLiveUIBlazor(options =>
{
    options.Discovery.Mode = LiveUIDiscoveryMode.CompiledFirst;
    options.Discovery.EnableRuntimeFallback = true;
    options.Interaction.EnableNavigation = true;
    options.Interaction.EnableFiltering = true;
    options.Interaction.EnableHighlighting = true;
    options.Security.RequireConfirmationForWrites = true;
});

builder.Services.AddScoped<ILiveUIActionAuthorizer, AppLiveUIActionAuthorizer>();
```

The policy must use trusted tenant, principal, session, route, and application permissions. Target capabilities describe available operations; they are not authorization grants. Keep existing controls and wrap the existing application content with `OpenConnectLiveUIProvider` inside the current OpenConnect composer/session context.

The `OpenConnectLiveUIProvider` loads the verified build manifest embedded by `OpenConnect.LiveUI.Compiler`; an explicit manifest can also be supplied for hosts that manage manifest loading themselves. The provider does not replace OpenConnectProvider, MiniComposer, the host router, or application components.

Embedded manifests are cached in-process by application assembly name, module version ID, and resource name. The loader validates the manifest before publishing the cached instance; repeated provider renders and circuits using the same application build reuse it. Explicitly supplied manifests are validated by the scoped manifest state.

`AddOpenConnectLiveUIBlazor` also registers the .NET MAF tool adapter and a scoped context accessor for the current circuit route. Create functions for an agent invocation with `LiveUIAgentToolFactory.CreateTools(scopedServices.GetRequiredService<LiveUIAgentTools>())`. Register a host `ILiveUIActionAuthorizer`; without one, the adapter denies actions. See the `OpenConnect.LiveUI.Agents` package documentation for tool names, authorization, and context requirements.

For a browser action requested by an agent, the provider captures the originating `ComposerSession`, binds its trusted session ID to the scoped LiveUI context for that asynchronous operation, invokes `LiveUIAgentTools`, and posts the result through `IOpenConnectClient`. If the composer session changes while an action is running, the provider cancels it and suppresses its stale receipt. The authenticated gateway binds accepted responses to the owned session and a matching pending request. The current default broker uses the in-memory session store, so the correlation is process-local.

## Runtime behavior

The browser runtime resolves targets by compiled selector, an optional LiveUI runtime ID, and accessible browser semantics. It rejects ambiguous matches. It supports focus, scroll, highlight, clear highlight, value entry, selection, tab activation, inspection, and bounded state waits. Navigation uses NavigationManager and succeeds only after the requested route and at least one compiled target are present.

Text entry into password or sensitive fields is denied. A dispatched click without an observable postcondition returns Uncertain; a receipt reports Completed only after the runtime verifies the requested state. Highlighting adds a non-modal outline, preserves focus, and respects reduced-motion settings.

To keep and transition highlights with narration, enable `Narration.Enabled` and `Narration.SynchronizeWithSpeech`. In this mode the .NET agent tools turn temporary durations into persistent highlights. `liveui_highlight` can supply a zero-based UTF-16 `speechStartCharacter` and `speechEndCharacter` range; the provider switches authorized targets when the audio player starts a segment overlapping that range. When cues overlap, every target is highlighted for that segment. If any requested target cannot be highlighted, the provider clears the partial set so narration does not continue with an incomplete cue. `MiniComposer` publishes audio-player lifecycle events through its cascading `ComposerSession`, and `OpenConnectLiveUIProvider` uses `LiveUISpeechPlaybackFence` to clear cues and highlights on response completion or interruption. No additional application callback wiring is required; `MiniComposer.SpeechPlaybackChanged` remains available for host-specific listeners. The `openconnect.liveui.speech.highlight.offset` histogram records milliseconds from the scheduled browser audio start until the provider has a verified highlight receipt; unchanged active targets record zero. This does not include acoustic output latency. Range alignment is at audio-segment boundaries and depends on the agent supplying correct offsets; word-level alignment is not provided. When speech synchronization is disabled, requested temporary highlight durations are honored by the browser runtime.

The `OpenConnect.LiveUI.Blazor` NuGet package includes the .NET Razor compiler and a `buildTransitive` MSBuild target. For Razor SDK applications, referencing the package generates and embeds the versioned manifest before resource naming; no separate compiler package or app-specific compiler command is required. Source-project integrations can use the Todo sample's explicit compiler project reference and target. Static analysis cannot infer all runtime component structure; unresolved or ambiguous targets remain explicit failures.
