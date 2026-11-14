# OpenConnect.LiveUI.Blazor

The Blazor adapter supplies a scoped manifest state, browser action runtime, and the popup-free DOM interaction module. It composes with the application's existing controls and does not require replacing or annotating every control.

## Register and bootstrap

Call AddOpenConnectLiveUIBlazor during service registration and configure navigation, filtering, and highlighting as required. Register a host ILiveUIActionAuthorizer before enabling actions. If no host policy is registered, the adapter denies actions.

Supply the verified build manifest through the OpenConnectLiveUIProvider component. The provider only supplies manifest state. It does not replace OpenConnectProvider, MiniComposer, the host router, or application components.

## Runtime behavior

The browser runtime resolves targets by compiled selector, an optional LiveUI runtime ID, and accessible browser semantics. It rejects ambiguous matches. It supports focus, scroll, highlight, clear highlight, value entry, selection, tab activation, inspection, and bounded state waits. Navigation uses NavigationManager and succeeds only after the requested route and at least one compiled target are present.

Text entry into password or sensitive fields is denied. A dispatched click without an observable postcondition returns Uncertain; a receipt reports Completed only after the runtime verifies the requested state. Highlighting adds a non-modal outline, preserves focus, and respects reduced-motion settings.

The application must supply a manifest. The Razor build compiler and automatic manifest loading are separate integration work; this package does not claim to generate a manifest on its own.
