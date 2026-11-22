---
title: Getting started
version: v1
summary: Understand the host, gateway and client, then make your first task request.
order: 10
---

# Getting started

OpenConnect runs in your .NET application. The host owns authentication, tenant identity, privacy policy and model or worker connections. An optional ASP.NET Core gateway exposes its API; clients call that gateway over HTTP. Installing a client does not create a host or provide a model.

## Choose a starting point

If you already have an authenticated OpenConnect gateway, follow the [.NET client](/docs/v1/dotnet-client) or [TypeScript client](/docs/v1/typescript-client) guide. You need its API base URL, a credential accepted by the host and permission for the requested operation.

If you are building the host, install the packages in [installation](/docs/v1/installation), then register the framework and gateway in an ASP.NET Core application:

To verify core registration independently, this small console example can be compiled and run:

```csharp runnable
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Core;

var services = new ServiceCollection();
services.AddOpenConnect();
Console.WriteLine(services.Count);
```

For an ASP.NET Core gateway, the composition is:

```csharp
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenConnect();
builder.Services.AddOpenConnectTaskExecution();

// Configure the host's authentication, authorization and task executor here.
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenConnect();
app.Run();
```

This is a composition outline, not a complete authenticated server: configure an authentication scheme and authorization, a real executor and any required model, persistence and privacy policy before accepting requests. The host derives tenant and principal from authenticated identity; do not accept these as authority from client-supplied JSON. See [configuration](/docs/v1/configuration) and [privacy](/docs/v1/privacy).

## Submit and observe a task

Create a unique client task ID and idempotency key, submit a message, save the returned task ID and query its state. Reuse the same idempotency key for a retry of the same logical request; a new operation needs a new key. Task acceptance is not proof that an agent or downstream side effect completed. The client guides include concrete calls and event replay.

The gateway's `/openconnect/v1/health` checks basic service liveness. Authenticated readiness and capability checks provide additional information, but a ready capability is not authorization to use it. See [REST routes](/docs/v1/rest-api).

## Add the services you need

Use the [service catalog](/docs/v1/supported-services) to select persistence, agent, UI and worker integrations. Registration alone does not create databases, provision NATS, install models or start workers. The [Todo sample](/tutorials/todo) shows a separate Blazor consumer; the source repository also contains persistence and worker samples.

Browse the [examples and sample projects](/docs/v1/examples) for runnable provider and worker starting points, then use the matching package guide for production configuration and security boundaries.

## Related links

- [.NET client](/docs/v1/dotnet-client)
- [TypeScript client](/docs/v1/typescript-client)
