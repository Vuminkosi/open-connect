---
title: Troubleshooting and deployment
version: v1
summary: Diagnose common startup, gateway, worker and documentation issues.
order: 100
---

# Troubleshooting

## Documentation does not load

Check `/health/live` and `/health/ready`. The portal's static content does not require a gateway, database, NATS or worker. A readiness failure means the content index did not load.

## Composer shows unavailable

The assistant/composer is optional. Verify the configured gateway URL, HTTPS policy, authentication and `/openconnect/v1/health/ready`. Static documentation should remain available when the gateway is unavailable.

## Worker is unavailable

Check the worker's readiness endpoint and capability manifest. Do not treat a process liveness response as model readiness. Missing required detector coverage must fail closed rather than downgrade privacy.

## Request is unauthorized or forbidden

Confirm that the client is using the application gateway's `/openconnect/v1` base URL, that the credential is valid for that host, and that the authenticated identity maps to the expected tenant and principal. Check the host's policy for the operation. Do not add tenant identity to request JSON as a workaround. For a WebSocket, verify its authentication separately: the TypeScript client's HTTP bearer token is not attached by the browser WebSocket constructor.

## Task was accepted but has not completed

Read the task by its returned ID and inspect its session events and executor diagnostics without logging sensitive content. Check model provider configuration, worker readiness, privacy policy and outbox delivery where used. Acceptance only confirms the gateway accepted the operation; it does not confirm a model response or an external side effect. Before retrying after a timeout, use the original idempotency key and reconcile the operation's state.

## Privacy or tool request is denied

For `privacy_blocked`, inspect the configured destination policy and detector readiness. Do not retry by sending the original content around the gate. For `tool_not_allowed`, check registration, agent allowlist and current tenant, principal and task authorization. For `approval_required`, obtain the appropriate host-issued approval for the exact operation; model output cannot grant it.

## Database or outbox is not ready

Confirm that the correct provider and application context are registered, the connection reaches the intended database, and reviewed migrations have been applied by the application owner. Check pending outbox items and worker connectivity separately. Preserve the database and existing volumes while diagnosing. The InMemory provider is not a durable substitute.

## Missing events or replay failure

Use the correct session identifier for the chosen replay API and persist the last successfully processed sequence. Replay's `afterSequence` is exclusive. The .NET `SubscribeEventsAsync` call returns a finite snapshot; it does not reconnect automatically. A canonical `legacy_event_without_identity` error requires investigation of the stored event, not silently advancing the cursor.

## Related links

- [Configuration reference](/docs/v1/configuration)
- [Gateway HTTP API](/docs/v1/rest-api)
