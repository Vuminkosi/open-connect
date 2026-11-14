# OpenConnect.LiveUI.Blazor

The Blazor adapter supplies a scoped manifest state, browser action runtime, and the popup-free DOM interaction module. It composes with the application's existing controls and does not require replacing or annotating every control.

## Register and bootstrap

Call AddOpenConnectLiveUIBlazor during service registration and configure navigation, filtering, and highlighting as required. Register a host ILiveUIActionAuthorizer before enabling actions. If no host policy is registered, the adapter denies actions.

The `OpenConnectLiveUIProvider` loads the verified build manifest embedded by `OpenConnect.LiveUI.Compiler`; an explicit manifest can also be supplied for hosts that manage manifest loading themselves. The provider does not replace OpenConnectProvider, MiniComposer, the host router, or application components.

`AddOpenConnectLiveUIBlazor` also registers the .NET MAF tool adapter and a scoped context accessor for the current circuit route. Create functions for an agent invocation with `LiveUIAgentToolFactory.CreateTools(scopedServices.GetRequiredService<LiveUIAgentTools>())`. Register a host `ILiveUIActionAuthorizer`; without one, the adapter denies actions. See the `OpenConnect.LiveUI.Agents` package documentation for tool names, authorization, and context requirements.

## Runtime behavior

The browser runtime resolves targets by compiled selector, an optional LiveUI runtime ID, and accessible browser semantics. It rejects ambiguous matches. It supports focus, scroll, highlight, clear highlight, value entry, selection, tab activation, inspection, and bounded state waits. Navigation uses NavigationManager and succeeds only after the requested route and at least one compiled target are present.

Text entry into password or sensitive fields is denied. A dispatched click without an observable postcondition returns Uncertain; a receipt reports Completed only after the runtime verifies the requested state. Highlighting adds a non-modal outline, preserves focus, and respects reduced-motion settings.

The Razor build compiler is provided by the separate `OpenConnect.LiveUI.Compiler` project and must be wired into the consuming app's build. The Todo sample demonstrates manifest generation and embedding. Static analysis cannot infer all runtime component structure; unresolved or ambiguous targets remain explicit failures.
