# OpenConnect.Agents

MAF-first agent execution for OpenConnect: real Microsoft Agent Framework runtime agents
with an agent catalog, delegation gates, and run budgets layered above the MAF runtime
(`OpenConnect.Agents`, `OpenConnect.Agents.Catalog`). Unsupported orchestration profiles
fail closed before any model call is attempted.

## Install

```bash
dotnet add package OpenConnect.Agents
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;

services.AddOpenConnectAgent("support");

var registry = provider.GetRequiredService<IAgentDefinitionRegistry>();
AgentDefinition? agent = registry.Get(new AgentId("support"));
```

`AddOpenConnectAgent` (`Agents/Templates/AgentRegistrationExtensions.cs:16`) registers the
definition; use `AddOpenConnectLeadAgent` (`:32`) for the single lead agent. MAF-first:
orchestration profiles unsupported by the runtime fail closed before any model call.

## License

Same terms as the OpenConnect repository.
