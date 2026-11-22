---
title: Configuration reference
version: v1
summary: Configure OpenConnect with secure defaults and explicit optional infrastructure.
order: 60
---

# Configuration reference

`AddOpenConnect()` enables the host privacy firewall by default. Optional persistence, NATS, workers, Navigator and voice integrations require explicit registration and independent readiness evidence.

## Secure defaults

```csharp
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true;
});
```

Registration does not launch Docker, download models, create databases, provision streams or publish packages.

## Gateway configuration

The ASP.NET Core gateway exposes the OpenConnect session and execution endpoints under `/openconnect/v1`. Install `OpenConnect.Hosting.AspNetCore`, register the core services and task execution services, then map the endpoints in the host:

```csharp
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;

builder.Services.AddOpenConnect();
builder.Services.AddOpenConnectTaskExecution();

var app = builder.Build();
app.MapOpenConnect();
app.Run();
```

Configure authentication and authorization in the host before exposing the gateway. Optional workers and model providers must be configured and deployed separately; registering the endpoints does not start them. See the [gateway API reference](/docs/v1/rest-api) for the available HTTP operations.

## Capability status

Advertise only capabilities that are installed and ready. Disabled model-backed detectors must not load models or advertise readiness. A required privacy detector that is unavailable blocks the affected protected egress.

## What each registration does

| Registration | Effect | Additional host work |
| --- | --- | --- |
| `AddOpenConnect` | Registers core services and options | Configure identity, policy and optional integrations |
| `AddOpenConnectTaskExecution` | Registers task execution services | Configure a suitable executor and model integration |
| `MapOpenConnect` | Maps gateway endpoints | Configure authentication and authorization middleware |
| `AddOpenConnectClient` | Registers the typed HTTP client | Set the API base URL and provide caller authentication |
| `AddOpenConnectEntityFramework<TContext>` | Registers EF-backed persistence | Register the context and provider first; manage migrations |

Keep secrets in a deployment secret store, not configuration committed to source or client-side assets. Validate settings and connectivity at startup where possible. The host, gateway and workers can have different credentials and trust boundaries; a worker credential must not be treated as an end-user identity.

## Local HTTP and production transport

The `AddOpenConnectClient(Uri)` overload requires HTTPS. The options overload exposes `AllowInsecureHttp` for explicitly configured HTTP. Use it only in an appropriately isolated development environment; protect production transport and authenticate each request. Include `/openconnect/v1/` in the client base address. Gateway health and readiness are different checks: a successful liveness response does not establish model, database or worker readiness.

## Configuration order

Register the application's database context before its EF integration. Configure authentication and authorization before mapping protected gateway routes. Configure a model provider, an executor, policies and worker connections before expecting tasks to run. Deploy and check external dependencies separately. For concrete provider and migration examples, see [persistence](/docs/v1/persistence); for client configuration, see [.NET](/docs/v1/dotnet-client) and [TypeScript](/docs/v1/typescript-client).

## Related links

- [Getting started](/docs/v1/getting-started)
- [Supported services and integrations](/docs/v1/supported-services)
