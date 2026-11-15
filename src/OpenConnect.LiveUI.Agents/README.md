# OpenConnect.LiveUI.Agents

This .NET adapter exposes governed LiveUI operations as Microsoft Agent Framework tools. It does not introduce a second agent loop or execute model-generated scripts. .NET contracts and host policy are the primary integration surface; the browser runtime is provided by the Blazor adapter.

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

The Blazor provider binds each connected-client operation to the originating `ComposerSession` ID through the optional `ILiveUIAgentContextSessionBinder` contract. Hosts that use their own context accessor can implement this binder to scope trusted session identity to an asynchronous operation. Session replacement cancels in-flight actions and suppresses stale receipts.

Every action runs through `LiveUIOrchestrator` and `ILiveUIActionAuthorizer`. The default Blazor policy denies all actions until the host registers its own authorization policy. Bind that policy to authenticated principal, tenant, current session, and the application's action rules. Missing or failing policy denies the tool operation. A dispatched action is reported as complete only when the browser runtime verifies its postcondition.

Actions from the same scoped tool instance execute serially, so one browser acknowledgement completes before the next tool action begins. Cancellation is forwarded to the orchestrator and browser runtime.

`liveui_get_current_page` returns the current route, targets with individually authorized capabilities, and only routes the host policy permits this session to navigate to. If the current route is not inspectable, it returns no targets while still exposing any permitted navigation destinations. Target labels originate from application UI and are untrusted display text; they must not be promoted to instructions. No full application manifest is sent to the model.

When a host registers `IClientInteractionBroker`, `LiveUIClientAgentToolFactory.CreateTools` creates connected-client functions for current page, navigation, inspection, action execution, state waits, highlighting, and clearing highlights. These emit bounded, correlated session requests and wait for the Blazor provider to return an authorized result. The gateway validates session ownership, correlation, response kind, and payload bounds before releasing the response. The default broker is backed by the process-local in-memory session store; a distributed host must supply a shared implementation before using multiple gateway instances.

When the Blazor host enables speech synchronization, `liveui_highlight` may include both `speechStartCharacter` and `speechEndCharacter` as a zero-based UTF-16 range in the following assistant response. The provider switches among authorized targets when the actual audio player starts the segment whose character range overlaps the cue. Cues are limited to 32,000 characters and are cleared on completion, interruption, or cancellation. This coordinates at audio-segment boundaries; it does not provide word-level alignment or verify that the model chose a semantically correct target range.

When `ICapabilityRegistry` is registered by the host, creating the MAF tools refreshes the `liveui.agent-tools` capability as ready. The host can discover this through `GetReady(CapabilityKind.Tool)` before attaching the functions to an agent.
