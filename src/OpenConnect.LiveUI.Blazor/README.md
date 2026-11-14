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

For a browser action requested by an agent, the provider receives the correlated composer-session request, invokes scoped `LiveUIAgentTools`, and posts the result through `IOpenConnectClient`. The authenticated gateway binds the response to the owned session and a matching pending request. The current default broker uses the in-memory session store, so the correlation is process-local.

## Runtime behavior

The browser runtime resolves targets by compiled selector, an optional LiveUI runtime ID, and accessible browser semantics. It rejects ambiguous matches. It supports focus, scroll, highlight, clear highlight, value entry, selection, tab activation, inspection, and bounded state waits. Navigation uses NavigationManager and succeeds only after the requested route and at least one compiled target are present.

Text entry into password or sensitive fields is denied. A dispatched click without an observable postcondition returns Uncertain; a receipt reports Completed only after the runtime verifies the requested state. Highlighting adds a non-modal outline, preserves focus, and respects reduced-motion settings.

To keep a highlighted target visible for the actual narration playback, enable `Narration.Enabled` and `Narration.SynchronizeWithSpeech`. In this mode the .NET agent tools turn temporary highlight durations into persistent highlights; connect `MiniComposer.SpeechPlaybackChanged` to `LiveUISpeechPlaybackFence` and clear highlights when it reports response completion or interruption. The Blazor Todo sample demonstrates this lifecycle wiring. When speech synchronization is disabled, requested temporary highlight durations are honored by the browser runtime.

The Razor build compiler is provided by the separate `OpenConnect.LiveUI.Compiler` project and must be wired into the consuming app's build. The Todo sample demonstrates manifest generation and embedding. Static analysis cannot infer all runtime component structure; unresolved or ambiguous targets remain explicit failures.
