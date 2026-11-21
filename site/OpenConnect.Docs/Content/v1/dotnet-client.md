---
title: .NET client
version: v1
summary: Configure a gateway client, submit tasks and replay session events.
order: 12
---

# .NET client

Install `OpenConnect.Client` in the calling application and obtain an authenticated gateway URL ending in `/openconnect/v1/`. The client uses relative request paths, so use the API base path, not just the server origin. The URI registration overload requires HTTPS and rejects query strings and fragments.

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Client;

var services = new ServiceCollection();
services.AddOpenConnectClient(new Uri("https://gateway.example/openconnect/v1/"));
```

Configure the host's HTTP authentication mechanism on the registered HTTP client. Credentials must come from a secure source and must be scoped to the caller; do not put long-lived credentials in browser code. The API requires an authenticated tenant and principal. Registration does not authenticate a user or start a gateway.

For example, a server-side application with a host-approved service credential can configure the underlying typed client at registration:

```csharp
using System.Net.Http.Headers;

// serviceToken is obtained from the server's secret provider.
services.AddOpenConnectClient(
    new Uri("https://gateway.example/openconnect/v1/"),
    http => http.ConfigureHttpClient(client =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", serviceToken)));
```

Use this pattern only when the gateway is configured to accept that service identity. For per-user access, propagate the current user's credential using a request-scoped mechanism; do not store one user's bearer token in shared default headers. Credential issuance and authorization are host responsibilities.

Resolve `IOpenConnectClient` from the application's service provider and submit a task:

```csharp
using OpenConnect.Client;

// client is an IOpenConnectClient from dependency injection.
var task = await client.CreateTaskAsync(new CreateTaskRequest(
    ClientTaskId: "example-task-1",
    IdempotencyKey: "example-operation-1",
    Message: "Summarize the supplied document"), cancellationToken);

var current = await client.GetTaskAsync(task.Id, cancellationToken);
// For the same logical request, retain the original idempotency key when retrying.
```

Replace the example IDs with unique, persisted values. `ClientTaskId` is required and limited to 128 characters; `IdempotencyKey` is required and limited to 256. An optional `SessionId` binds a task to an existing session. A task response is an acceptance/state record, not the completed result. `CancelTaskAsync` requests cancellation; check the returned and subsequent state before assuming execution stopped.

## Create a session and submit a task

```csharp
var session = await client.CreateSessionAsync(cancellationToken);
var operationId = Guid.NewGuid().ToString("N");
var task = await client.CreateTaskAsync(new CreateTaskRequest(
    ClientTaskId: operationId,
    IdempotencyKey: operationId,
    Message: "Summarize this request",
    SessionId: session.Id.Value.ToString()), cancellationToken);

Console.WriteLine($"Task {task.Id.Value}: {task.State}");
```

Persist `operationId` before sending so a retry can reuse it. Acceptance does not mean execution completed.

## Read events and resume

```csharp
long cursor = 0; // Load the last successfully processed sequence on restart.
await foreach (var item in client.SubscribeEventsAsync(session.Id, cursor, cancellationToken))
{
    Console.WriteLine($"{item.Sequence}: {item.Type}");
    cursor = item.Sequence; // Persist only after processing succeeds.
}
// Call again with cursor to fetch later events.
```

This is a finite snapshot, not an automatic subscription. `ReplayEventsAsync` accepts a public session UUID, not a ULID-derived UUID; do not silently skip `legacy_event_without_identity`.

## List capabilities and cancel

### Check available capabilities

```csharp
var capabilities = await client.GetCapabilitiesAsync(cancellationToken);
foreach (var capability in capabilities)
    Console.WriteLine($"{capability.Id.Value}: {capability.State}");
```

Readiness does not grant authorization.

### Cancel a running task

```csharp
var cancellation = await client.CancelTaskAsync(task.Id, cancellationToken);
Console.WriteLine(cancellation.State);
```

Cancellation is a request. Check subsequent state and reconcile any external effects.

## Handle gateway errors

```csharp
try
{
    var current = await client.GetTaskAsync(task.Id, cancellationToken);
    Console.WriteLine(current.State);
}
catch (OpenConnectClientException ex)
{
    Console.Error.WriteLine($"Gateway error: {ex.StatusCodeValue}, {ex.ErrorCode}");
}
```

Treat authorization and privacy denials as decisions. For uncertain retries, preserve the original idempotency key.

## Related links

- [Gateway HTTP API](/docs/v1/rest-api)
- [TypeScript client](/docs/v1/typescript-client)
