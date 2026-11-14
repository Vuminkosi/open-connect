# OpenConnect.LiveUI.Agents

This .NET adapter exposes governed LiveUI operations as Microsoft Agent Framework tools. It does not introduce a second agent loop or execute model-generated scripts.

## Create MAF tools

Register the adapter alongside the host's LiveUI authorization policy and context accessor:

```csharp
services.AddOpenConnectLiveUIBlazor();
services.AddOpenConnectLiveUIAgentTools();

// In the scoped agent invocation:
var tools = LiveUIAgentToolFactory.CreateTools(
    scopedServices.GetRequiredService<LiveUIAgentTools>());
var response = await mafFactory.RunAsync(agentId, prompt, tools, cancellationToken);
```

Blazor registration supplies a scoped context accessor that binds the active circuit to an exact compiled route path. Dynamic route parameters are not inferred. Other .NET hosts call `AddOpenConnectLiveUI()` and `AddOpenConnectLiveUIAgentTools()`, implement `ILiveUIAgentContextAccessor`, and resolve the session and route from trusted host state. Tool arguments cannot supply a session ID, current route, manifest hash, or action ID.

Every action runs through `LiveUIOrchestrator` and `ILiveUIActionAuthorizer`. The default Blazor policy denies all actions until the host registers its own authorization policy. Bind that policy to authenticated principal, tenant, current session, and the application's action rules. Missing or failing policy denies the tool operation. A dispatched action is reported as complete only when the browser runtime verifies its postcondition.

Actions from the same scoped tool instance execute serially, so one browser acknowledgement completes before the next tool action begins. Cancellation is forwarded to the orchestrator and browser runtime.

`liveui_get_current_page` returns only the current route and targets individually authorized for inspection. Target labels originate from application UI and are untrusted display text; they must not be promoted to instructions. No full application manifest is sent to the model.

When `ICapabilityRegistry` is registered by the host, creating the MAF tools refreshes the `liveui.agent-tools` capability as ready. The host can discover this through `GetReady(CapabilityKind.Tool)` before attaching the functions to an agent.
