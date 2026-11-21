---
title: TypeScript client
version: v1
summary: Submit tasks and consume events from a browser or Node application.
order: 13
---

# TypeScript client

Install `@openconnect/client` from your configured registry. The package is ESM and supports browser fetch or Node 24 and later. Supply the gateway's full API base URL and a credential appropriate for the authenticated caller:

```ts
import { OpenConnectClient, OpenConnectError } from "@openconnect/client";

const client = new OpenConnectClient({
  baseUrl: "https://gateway.example/openconnect/v1",
  token: accessToken, // Obtain a short-lived caller credential securely.
});

try {
  const task = await client.createTask(
    { clientTaskId: "example-task-1", message: "Hello" },
    "example-operation-1",
  );
  const current = await client.getTask(task.id);
  console.log(current.state);
} catch (error) {
  if (error instanceof OpenConnectError) {
    console.error(error.code, error.status, error.correlationId);
  } else {
    throw error;
  }
}
```

`accessToken` is supplied by your application's authentication flow. Never embed a service credential in a distributed browser bundle. Replace the example IDs with unique values. `createTask` defaults the idempotency key to `clientTaskId` when omitted; preserve the original key when retrying the same operation. The request uses `message`, not the former `input`, and does not select an agent using `agentId`. The response ID is `id`. Use `cancelTask(id)` to request cancellation and check subsequent state.

## Capabilities and events

`listCapabilities()` lists advertised capabilities; `listReadyHostCapabilities()` lists ready, non-expired host registrations. Readiness is not permission or proof that a request will succeed.

### Replay and subscribe

```ts
const events = await client.replayEvents(sessionId, lastSequence);
// Process events and persist the last successfully processed sequence.
const unsubscribe = client.subscribeEvents(sessionId, {
  afterSequence: lastSequence,
  onEvent: event => console.log(event),
  onError: error => console.error(error.code),
});
// Call unsubscribe() when the view or session is no longer active.
```

Supply an existing session ID and a non-negative exclusive cursor; start at zero for the initial replay. Replay is a finite HTTP request. The subscription uses WebSocket and returns a close function; the application owns reconnection and cursor management. The HTTP `token` is sent as a bearer header by fetch, but the WebSocket constructor does not attach that header. Configure a host-supported WebSocket authentication mechanism and verify it in the target browser or runtime. Do not put credentials in URLs.

`OpenConnectError` includes `code`, HTTP `status`, optional `correlationId` and `retryable`. A retryable transport or server error is not proof that the original request had no effect. Use the original idempotency key and reconcile state before retrying. See [troubleshooting](/docs/v1/troubleshooting).

## List capabilities

```ts
const ready = await client.listReadyHostCapabilities();
for (const capability of ready) {
  console.log(capability.id.value, capability.state);
}
```

## Cancel a task

```ts
const result = await client.cancelTask(taskId);
console.log(result.state);
// Check state again: cancellation does not undo an external side effect.
```

## Use a custom authenticated fetch

```ts
const clientWithAuth = new OpenConnectClient({
  baseUrl: "https://gateway.example/openconnect/v1",
  fetchImpl: async (input, init) => {
    const headers = new Headers(init?.headers);
    headers.set("Authorization", `Bearer ${await getAccessToken()}`);
    return fetch(input, { ...init, headers });
  },
});
```

Implement `getAccessToken()` using your application's authentication flow. WebSockets require their own host-supported authentication.

## Retry a task submission

### Preserve the operation key

```ts
const operationId = crypto.randomUUID();
const request = { clientTaskId: operationId, message: "Summarize this item" };
// Persist operationId and the request before submitting if a restart must resume it.
try {
  const task = await client.createTask(request, operationId);
  console.log(task.id, task.state);
} catch (error) {
  if (!(error instanceof OpenConnectError) || !error.retryable) throw error;
  // Reconcile the first attempt according to the host's policy before retrying.
  // If retrying the same operation, use request and operationId again.
}
```

## Related links

- [Gateway HTTP API](/docs/v1/rest-api)
- [Composer and UI integration](/docs/v1/ui-integration)
