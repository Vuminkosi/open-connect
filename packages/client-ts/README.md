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
  { clientTaskId: "client-1", sessionId: "<uuid>", agentId: "agent-a", input: "hello" },
  "idempotency-key-1",
);
const current = await client.getTask(task.taskId);
const capabilities = await client.listCapabilities();
const events = await client.replayEvents("<session-uuid>", 0);
const unsubscribe = client.subscribeEvents("<session-uuid>", { onEvent: console.log });
unsubscribe();
```

Errors are thrown as `OpenConnectError` with kebab-case `code`
(`task_not_found`, `unauthorized`, `conflict`, `rate_limited`, …),
numeric `status`, and `retryable` for 429/5xx.
