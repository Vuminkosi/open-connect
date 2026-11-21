---
title: Composer and UI integration
version: v1
summary: Add a composer and guided navigation to Blazor or browser applications.
order: 42
---

# Composer and UI integration

The UI packages compose with an existing application and an authenticated OpenConnect gateway. They do not create a model or grant permission to act on the user's behalf. Choose the adapter that matches your application and supply a real session and client.

## Blazor

Install `OpenConnect.Components.Blazor` and configure the typed gateway client. Register the component services:

```csharp
using OpenConnect.Components.Blazor;

builder.Services.AddOpenConnectComponents();
builder.Services.AddOpenConnectComposer();
```

The application owns the MudBlazor theme, dialog, snackbar and popover providers. Compose the OpenConnect provider and `MiniComposer` into the application's existing component tree and use the authenticated session. The repository's `samples/Blazor` shows the full wiring, including gateway mode and LiveUI authorization. Confirm the correct static assets, interactive render mode, authentication and gateway connection for your host. A visible composer does not prove that task execution, voice or a worker is available.

### Documentation site example

The documentation site registers `AddOpenConnectComponents` and its `AddDocumentationAssistant` feature, which configures the OpenConnect client, composer, LiveUI authorizer and optional worker planner. Its `SiteAssistant` component places `MiniComposer` inside `OpenConnectProvider` and `OpenConnectLiveUIProvider`, with a manifest limited to public documentation routes. The host supplies matching light and dark MudBlazor palettes and follows the site theme.

Visitors can read the documentation without signing in. When the assistant is enabled with visitor identity, the site issues a separate, short-lived identity for each visitor circuit and keeps its gateway credential on the server. The application gateway owns session authorization and privacy enforcement. Voice input additionally requires a local transcription model and a configured voice path; a ready text assistant alone does not establish that voice is available.

## Browser and React

The `@openconnect/composer` package provides a framework-neutral composer and an optional React adapter. Install compatible published releases of the composer and client packages. In a browser application:

```ts
import { createMiniComposer } from "@openconnect/composer";
import "@openconnect/composer/styles.css";
import { OpenConnectClient } from "@openconnect/client";

const client = new OpenConnectClient({ baseUrl: "https://gateway.example/openconnect/v1" });
const composer = createMiniComposer({
  client,
  sessionId: existingSessionId,
  agentId: configuredAgentId,
  assistantName: "Assistant",
});
composer.mount(document.body);
// On application teardown: composer.dispose();
```

Supply the existing session and configured agent identifiers and arrange host-supported authentication. The composer uses task and session events for its status; integrate event handling and error display with your application. `agentId` here is a composer option, not an `agentId` field in the task submission HTTP body. See the package README for React provider composition, styling and lifecycle methods. Avoid placing server secrets in browser code.

## Guided actions

Navigator provides guided tours; LiveUI provides typed application actions and receipts. Register a host `ILiveUIActionAuthorizer`, tie it to the authenticated session and application permissions, and require confirmation where the action warrants it. The default authorizer denies actions. See [LiveUI](/docs/v1/liveui) for inference and receipt behavior. Test keyboard operation, focus, narrow screens and reduced motion in the actual host application.

```ts
import { createOpenConnectNavigator } from "@openconnect/navigator";

const guide = createOpenConnectNavigator({
  baseUrl: "https://gateway.example/openconnect/v1",
  routeId: "/editor",
  speech: { enabled: false },
  capture: { enabled: false },
});
guide.registry.register({
  targetId: "save",
  routeId: "/editor",
  label: "Save",
  description: "Save your changes.",
});
await guide.start();
const receipt = await guide.dispatch({ intent: "highlight", targetId: "save" });
console.log(receipt.outcome);
// When the page unmounts: guide.dispose();
```

Navigation and capture need host authorization. Provide the package's supported authenticated request configuration for server-backed actions.

## Related links

- [LiveUI interaction and inference](/docs/v1/liveui)
- [Todo sample tutorial](/docs/v1/todo-sample)
