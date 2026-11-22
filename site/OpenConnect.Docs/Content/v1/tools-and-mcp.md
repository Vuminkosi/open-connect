---
title: Tools, approvals and MCP
version: v1
summary: Register read-only or effectful tools through the trusted host authorization boundary.
order: 80
---

# Tools, approvals and MCP

Tool discovery is filtered by the agent allowlist, principal grants, tenant policy and task context. Every invocation is reauthorized at the effect boundary.

Model output and tool results never grant permissions. Unregistered tools, malformed arguments, forged approvals and unsafe destinations must fail closed with stable error codes such as `tool_not_allowed`, `approval_required` or `privacy_blocked`.

Remote MCP descriptions and tool schemas are untrusted input. The host must enforce destination, scope, privacy and approval policy independently.

## Register and discover

`ToolCatalog.Register` takes a `ToolRegistration` containing a descriptor, JSON object schema and asynchronous handler, plus an explicit list of allowed agent IDs. The descriptor's agent must match the invoking agent. Tool IDs are validated, duplicate IDs are rejected, and the schema must describe an object. Keep handlers narrow and validate domain constraints and destination access inside the handler as well.

```csharp
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Tools;

// catalog uses the host's IToolAccessPolicy; it is not an authorization bypass.
var agentId = new AgentId("support");
var toolId = new ToolId("lookup-order");
var descriptor = new ToolDescriptor(toolId, "Look up an order", agentId);
const string schema = """
    { "type": "object", "properties": { "orderId": { "type": "string" } },
      "required": ["orderId"], "additionalProperties": false }
    """;

var registration = new ToolRegistration(descriptor, schema, async (args, ct) =>
{
    string orderId = args.GetProperty("orderId").GetString()!;
    // orders must re-check access to this particular order for the current caller.
    var order = await orders.FindAuthorizedAsync(orderId, ct);
    return JsonSerializer.SerializeToElement(order);
});
catalog.Register(registration, allowedAgents: [agentId]);

var visible = await catalog.DiscoverAsync(agentId, principalId, tenantId,
    taskId, cancellationToken);
```

Use `OpenConnect.Tools` and `OpenConnect.Abstractions` namespaces. Discovery intersects the agent allowlist with current authorization for the principal, tenant and task. The legacy `GetAuthorized` method lacks task context and returns no tools; use `DiscoverAsync` for policy-aware discovery.

## Invoke and approve

Call `ToolBroker.InvokeAsync` with a trusted `ToolAccessRequest`, JSON arguments and, when required, a host-issued approval. The broker checks registration, agent allowlist and current authorization again, checks the argument object's declared required fields and basic types, validates approval and then invokes the handler. It rejects undefined or oversized output. This schema check is not a complete JSON Schema implementation; enforce additional constraints in your handler or a dedicated validator.

An approval must correspond to the authorized request and arguments; a model cannot issue one. Recheck authorization at the side-effect boundary and design effectful handlers for idempotency. A timeout or lost response can occur after the handler has acted, so reconcile before repeating it. MCP tool descriptions and remote results need the same scrutiny as any other untrusted input.

```csharp
var request = new ToolAccessRequest(agentId, toolId, principalId, tenantId, taskId);
using var arguments = JsonDocument.Parse("""{"orderId":"order-123"}""");
var result = await broker.InvokeAsync(request, arguments.RootElement,
    cancellationToken: cancellationToken);
```

`orders`, `catalog`, `broker` and the identity values above are application-provided. Obtain identity from trusted authentication, not model arguments.

## Related links

- [Privacy firewall](/docs/v1/privacy)
- [Microsoft Agent Framework integration](/docs/v1/maf-integration)
