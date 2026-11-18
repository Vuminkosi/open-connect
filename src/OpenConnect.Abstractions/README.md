# OpenConnect.Abstractions

Dependency-free contracts for OpenConnect: stable IDs, DTOs, error codes, and the core
interfaces (`OpenConnect.Abstractions`) shared by every layer. This package has no external
dependencies by design, so all other OpenConnect packages can reference it safely.

## Install

```bash
dotnet add package OpenConnect.Abstractions
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Abstractions;

var registry = provider.GetRequiredService<IAgentDefinitionRegistry>();
registry.Register(new AgentDefinition(new AgentId("support"), "Support"));

AgentDefinition? agent = registry.Get(new AgentId("support"));
```

`AgentDefinition` (`DTOs.cs:7`) and `IAgentDefinitionRegistry.Register` (`Interfaces.cs:5`)
are the contract surface; concrete behavior lives in the companion packages.

## License

Same terms as the OpenConnect repository.
