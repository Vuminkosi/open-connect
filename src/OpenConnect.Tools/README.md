# OpenConnect.Tools

Tool plane for OpenConnect: catalog, broker, and per-call authorization
(`OpenConnect.Tools`) governing which tools agents may invoke.

## Install

```bash
dotnet add package OpenConnect.Tools
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Tools;

catalog.Register(
    new ToolRegistration(descriptor, jsonSchema, InvokeAsync: async (args, ct) => result),
    allowedAgents: [new AgentId("support")]);

JsonElement result = await broker.InvokeAsync(request, arguments);
```

`ToolCatalog.Register` (`ToolCatalog.cs:16`) allowlists a `ToolRegistration` (`:8`) per
agent; `ToolBroker.InvokeAsync` (`ToolBroker.cs:16`) re-checks registration, policy,
schema, and approval on every call and throws `ToolAuthorizationException` on denial.

## License

Same terms as the OpenConnect repository.
