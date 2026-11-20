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
