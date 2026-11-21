---
title: Gateway HTTP API
version: v1
summary: Gateway routes, authentication, task submission and event replay.
order: 55
---

# Gateway HTTP API

The application gateway maps routes under `/openconnect/v1`. Its versioned contract is maintained in `contracts/openapi/openconnect.v1.yaml` in the source repository; generated contract artifacts are available when the portal API generation step has run. Authenticate using the host's configured scheme. Tenant and principal scope come from trusted identity, not request JSON.

| Method and path | Purpose |
| --- | --- |
| `GET /health` | Basic liveness |
| `GET /health/ready` | Authenticated gateway readiness |
| `POST /sessions` | Create a session |
| `GET /sessions/{id}` | Read a session |
| `GET /sessions/{id}/events?afterSequence=0` | Replay events after an exclusive sequence cursor |
| `POST /tasks` | Submit a task with an `Idempotency-Key` header |
| `GET /tasks/{id}` | Read task state |
| `POST /tasks/{id}/cancel` | Request cancellation |
| `GET /capabilities` | List advertised capabilities |
| `GET /capabilities/ready` | List ready, non-expired host capabilities |
| `GET /components` | List component descriptors |
| `POST /speech/synthesize` | Synthesize speech when the host has configured it |

Paths in the table are relative to `/openconnect/v1`. Health is public; the other listed endpoints require authorization. The host also maps MiniComposer and LiveUI interaction endpoints. Optional voice, Navigator and MCP routes depend on host configuration; do not infer availability from the route name.

For task submission send JSON containing a required `clientTaskId`, optional `message`, and optional existing `sessionId`. Include the `Idempotency-Key` header. Save the returned task `id`; query its state or consume session events to observe progress. Retrying the same logical request requires the same key. Treat cancellation as a request, not proof that side effects were undone.

For example, from a trusted shell with an access token supplied through the environment:

### Submit a task

```bash
curl --fail-with-body https://gateway.example/openconnect/v1/tasks \
  -H "Authorization: Bearer $OPENCONNECT_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: example-operation-1" \
  -d '{"clientTaskId":"example-task-1","message":"Hello"}'
```

Use a fresh task ID and key for each new operation. Treat the token as a secret and use your deployment's secure credential mechanism; shell history and process inspection can expose command arguments. A successful response returns a task record, not necessarily a completed result. To read it, send an authenticated `GET` to `/openconnect/v1/tasks/{id}` using the returned `id`.

### Check task status

```bash
curl --fail-with-body "https://gateway.example/openconnect/v1/tasks/$TASK_ID" \
  -H "Authorization: Bearer $OPENCONNECT_TOKEN"
```

### Create a session

```bash
curl --fail-with-body https://gateway.example/openconnect/v1/sessions \
  -H "Authorization: Bearer $OPENCONNECT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Support conversation"}'
```

Save the returned session ID for task submission and replay.

### Request cancellation

```bash
curl --fail-with-body -X POST "https://gateway.example/openconnect/v1/tasks/$TASK_ID/cancel" \
  -H "Authorization: Bearer $OPENCONNECT_TOKEN"
```

Check the subsequent task state; cancellation does not reverse completed effects.

### Replay session events

```bash
curl --fail-with-body "https://gateway.example/openconnect/v1/sessions/$SESSION_ID/events?afterSequence=0" \
  -H "Authorization: Bearer $OPENCONNECT_TOKEN"
```

Use the last successfully processed sequence as the next exclusive cursor. Supply an existing session ID from the gateway.

Errors have stable codes including `unauthorized`, `task_not_found`, `session_not_found`, `invalid_request`, `conflict`, `rate_limited`, `tool_not_allowed`, `approval_required`, `capability_unavailable` and `privacy_blocked`. Handle authorization and privacy denials as decisions, not transient failures. Reconcile uncertain operations before retrying. See [.NET](/docs/v1/dotnet-client) and [TypeScript](/docs/v1/typescript-client) examples.

## Related links

- [.NET client](/docs/v1/dotnet-client)
- [TypeScript client](/docs/v1/typescript-client)
