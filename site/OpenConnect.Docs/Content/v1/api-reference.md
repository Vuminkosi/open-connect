---
title: How to use the API reference
version: v1
summary: Choose an SDK, find an operation and understand its request and response.
order: 50
---

# How to use the API reference

Use the [API reference](/api) to find an operation. Choose the interface your application uses:

| Your application | Start here | What you call |
| --- | --- | --- |
| .NET application | [.NET client](/docs/v1/dotnet-client) | Methods on `IOpenConnectClient` |
| Browser or Node application | [TypeScript client](/docs/v1/typescript-client) | Methods on `OpenConnectClient` |
| Any HTTP client | [HTTP API](/docs/v1/rest-api) | Gateway URLs under `/openconnect/v1` |
| ASP.NET Core host | [Getting started](/docs/v1/getting-started) | Register services and map gateway routes |

The SDK clients call the same gateway. Install and configure an authenticated gateway separately before running these examples.

## Example: create a task

### .NET

```csharp
using OpenConnect.Client;

// client is an authenticated IOpenConnectClient resolved from DI.
var operationId = Guid.NewGuid().ToString("N");
var task = await client.CreateTaskAsync(new CreateTaskRequest(
    ClientTaskId: operationId,
    IdempotencyKey: operationId,
    Message: "Hello"), cancellationToken);
Console.WriteLine($"{task.Id.Value}: {task.State}");
```

### TypeScript

```ts
const operationId = crypto.randomUUID();
const task = await client.createTask(
  { clientTaskId: operationId, message: "Hello" },
  operationId,
);
console.log(task.id, task.state);
```

`client` must be configured as shown in the relevant client guide. Save the operation ID before sending; use the same idempotency key for a retry of that operation. The returned task is an acceptance record, not necessarily a completed result.

## Example: check progress

### .NET

```csharp
var current = await client.GetTaskAsync(task.Id, cancellationToken);
Console.WriteLine(current.State);
```

### TypeScript

```ts
const current = await client.getTask(task.id);
console.log(current.state);
```

To read session events or cancel a task, follow the client guides. The .NET `SubscribeEventsAsync` method returns a finite snapshot; call it again with the last processed sequence to retrieve later events.

## What the reference shows

- **Method or route:** what to call, such as `CreateTaskAsync` or `POST /tasks`.
- **Inputs:** values you supply, such as a message and idempotency key.
- **Response:** the task, session, events or error returned by the gateway.
- **Requirements:** authentication and any configured optional service.

The optional generated .NET metadata on [the API reference page](/api) contains raw type and signature data. If it is missing in a site build, the guides and examples are still available. For detailed HTTP paths and error codes, use the [HTTP API guide](/docs/v1/rest-api).

## Related links

- [.NET client](/docs/v1/dotnet-client)
- [TypeScript client](/docs/v1/typescript-client)
- [Gateway HTTP API](/docs/v1/rest-api)
- [Examples and sample projects](/docs/v1/examples)
