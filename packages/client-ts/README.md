# @openconnect/client

Fetch-based TypeScript SDK for the OpenConnect v1 HTTP contract
(`contracts/openapi/openconnect.v1.yaml`). Browser + Node 24, ESM with types.
Speaks only HTTP/WebSocket — it never exposes NATS to consumers.

## Usage

```ts
import { OpenConnectClient } from "@openconnect/client";

const client = new OpenConnectClient({
  baseUrl: "https://host/openconnect/v1",
  token: process.env["OPENCONNECT_TOKEN"],
});

const task = await client.createTask(
  { clientTaskId: "client-1", sessionId: "<session-ulid>", message: "hello" },
  "idempotency-key-1",
);
const current = await client.getTask(task.id);
const hostCapabilities = await client.listReadyHostCapabilities();
const events = await client.replayEvents("<session-ulid>", 0);
const unsubscribe = client.subscribeEvents("<session-ulid>", { onEvent: console.log });
unsubscribe();
```

After the stream connects, the host sends a `registered` control frame
(`{"type":"registered","connectionId":"...","sessionId":"..."}`). `subscribeEvents`
no longer passes this frame to `onEvent`; supply the optional `onRegistered`
callback to receive it as a `SessionRegistration` (`connectionId`, `sessionId`).
Without the callback the frame is ignored. Frames that are neither a valid
`registered` frame nor a well-formed event are reported to `onError` as
`invalid_request` and are not delivered to `onEvent`. Callers that previously
filtered `type === "registered"` inside `onEvent` can remove that check.

UUID session routes deliver canonical `SessionEvent` frames. ULID session routes
deliver the legacy host record (`sessionId`, `sequence`, `type`, `data`), which is
passed to `onEvent` unchanged as before. Unlike the .NET client, which skips records
it cannot deserialize, the TypeScript client reports unrecognized frames to `onError`.

```ts
client.subscribeEvents("<session-ulid>", {
  onRegistered: ({ connectionId }) => console.log("connected", connectionId),
  onEvent: console.log,
  onError: console.error,
});
```

`listReadyHostCapabilities()` reads the authenticated host registry across all
kinds. Descriptors contain the logical ID under `id.value` and include only ready,
non-expired registrations. Registration and worker readiness remain the host
application's responsibility. The existing `listCapabilities()` method is retained
for compatibility.

Task submission sends `clientTaskId`, optional `message`, and an optional existing
session ULID. Task records contain `id`, `tenantId`, `principalId`, and the host's
kebab-case lifecycle `state`; the IDs are canonical ULIDs. The former `input` and
`agentId` fields and `taskId` response member did not match the host. Migrate callers
to `message` and `id`; task submission does not currently select an agent by ID.

Errors are thrown as `OpenConnectError` with kebab-case `code`
(`task_not_found`, `unauthorized`, `conflict`, `rate_limited`, …),
numeric `status`, and `retryable` for 429/5xx.
