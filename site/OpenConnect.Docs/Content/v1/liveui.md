---
title: LiveUI interaction and inference
version: v1
summary: Discover application controls, authorize actions and configure local inference for LiveUI.
order: 65
---

# LiveUI interaction and inference

LiveUI connects an agent to an existing application's controls through typed actions and receipts. The Blazor adapter uses a compiled manifest and can discover additional visible controls at runtime. An action is limited to the current route, the available target capabilities and the host's authorization policy.

## Authorization and confirmation

Capabilities describe what a target can do; they do not grant permission. The default Blazor action authorizer denies actions until the host supplies a policy. A policy should use the trusted tenant, principal, session, route and application permissions. Consequential targets can require confirmation, and approval does not bypass host authorization.

## Add the Blazor adapter

Install `OpenConnect.LiveUI.Blazor`, register an application implementation of `ILiveUIActionAuthorizer`, and configure the adapter. The application authorizer in this example is yours to implement:

```csharp
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;

builder.Services.AddScoped<ILiveUIActionAuthorizer, AppLiveUIActionAuthorizer>();
builder.Services.AddOpenConnectLiveUIBlazor(options =>
{
    options.Discovery.EnableRuntimeFallback = true;
    options.Interaction.EnableNavigation = true;
    options.Interaction.EnableHighlighting = true;
    options.Security.RequireConfirmationForWrites = true;
});
```

Place `OpenConnectLiveUIProvider` around the application content inside the existing composer/session context. The provider uses a verified compiled manifest or an explicitly supplied manifest. Your policy must authorize each action against the actual user and application state. Test the integrated control and receipt behavior in a browser; registration does not establish that controls are discoverable or actions are safe.

### Try it on this site

Open the [privacy firewall guide](/docs/v1/privacy) and expand the assistant. Ask it to select the ordinary privacy example, then ask it to check the example. The selector contains only fixed synthetic messages; the local rules firewall evaluates the selected message without sending it to a model or worker. The assistant can also search the documentation index and the generated API catalog when that catalog is available.

These controls are explicitly listed in the site's manifest and constrained by its authorizer. The privacy selector permits only the two predefined values, and the example button is authorized only on the privacy guide. Cloud management actions remain user-operated. If an action is denied or uncertain, inspect the current page before trying again; do not infer success from the assistant's wording alone.

## Local inference

The inference router supports browser, local-worker and gateway providers. Providers are independently enabled; the browser, worker and gateway options are disabled by default. A local worker is accessed through an authenticated gateway, which owns its endpoint and credential and applies the privacy boundary. The worker must be installed and ready before it can provide plans.

For example, a host that has installed the private worker can enable the worker route:

```csharp
using OpenConnect.LiveUI.Inference;

services.AddOpenConnectLiveUIInference(options =>
{
    options.Mode = LiveUIInferenceMode.BrowserFirst;
    options.Worker.Enabled = true;
});
```

Registration does not provision the worker or its model. Configure the worker endpoint and credential in the gateway deployment, and keep credentials out of browser clients. A gateway model fallback requires its own explicitly registered provider and privacy controls. Browser-only mode disables server fallback.

## Plans and receipts

Inference produces a proposed plan; it does not authorize or execute actions. LiveUI validates the plan, checks each action against host policy, executes in the connected client and returns a correlated receipt. A visible state change does not by itself establish persistence, causation or uniqueness across hidden pages. An uncertain action may already have happened and should not be repeated without reconciliation.

## Related links

- [Composer and UI integration](/docs/v1/ui-integration)
- [Privacy firewall](/docs/v1/privacy)
- [Configuration reference](/docs/v1/configuration)
- [Gateway HTTP API](/docs/v1/rest-api)
- [Worker integration](/docs/v1/nats-and-workers)
