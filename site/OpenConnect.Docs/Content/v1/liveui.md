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

## Related documentation

Read the [configuration reference](/docs/v1/configuration), [gateway API reference](/docs/v1/rest-api) and [worker integration guide](/docs/v1/nats-and-workers) for the surrounding host and deployment boundaries.
