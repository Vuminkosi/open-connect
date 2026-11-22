---
title: Microsoft Agent Framework integration
version: v1
summary: Compose native Direct, Harness and Workflow capabilities without a second runtime.
order: 30
---

# Microsoft Agent Framework integration

OpenConnect uses the pinned Microsoft Agent Framework packages for agent intelligence, Harness planning and native workflows. OpenConnect owns application identity, authorization, privacy, task state, outbox and client projections.

## Ownership

Do not implement a competing planner, todo engine, LLM loop or workflow replayer. If a native MAF capability is unavailable for a target framework, expose it as unavailable rather than simulating it.

## Register an agent definition

Install `OpenConnect.Agents` alongside the host packages. Register the framework and a named definition at startup:

```csharp
using OpenConnect.Core;
using OpenConnect.Agents.Templates;

builder.Services.AddOpenConnect();
builder.Services.AddOpenConnectAgent("support", agent =>
{
    agent.Name = "Support assistant";
    agent.Version = "1";
});
```

The definition builder also exposes model requirements, tool, context and memory policies, budget, execution profile and mode. Its default profile is `Direct` and its default mode is `Interactive`. `AddOpenConnectLeadAgent` registers a lead definition and rejects a second lead; `AddOpenConnectAgentTemplate` registers a reusable template. Duplicate IDs are rejected during registration. Select and test a profile supported by your actual MAF runtime and target framework.

This creates metadata in the agent catalog. It does not configure a model, install a provider, authorize tools or start a gateway executor. Configure those separately, supply the host's authenticated context and test a real execution. Agent instructions and model outputs are data, not authorization policies.

## Run and observe work

An application normally submits a task through the gateway, observes its task state and processes session events. See [getting started](/docs/v1/getting-started) and the [.NET client](/docs/v1/dotnet-client). A task can be accepted before model execution completes. Use cancellation and resource budgets, and reconcile uncertain operations before retrying. Tool permissions remain subject to the host checks in [tools and MCP](/docs/v1/tools-and-mcp).

For an example combining a gateway, agent and execution registration, inspect `samples/workers/shared/OpenConnect.Samples.Workers.Host` in the source repository. It is a sample configuration, not a substitute for a production identity, privacy or model policy.

## Related links

- [Tools, approvals and MCP](/docs/v1/tools-and-mcp)
- [Configuration reference](/docs/v1/configuration)
