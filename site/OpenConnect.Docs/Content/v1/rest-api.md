---
title: REST and worker API reference
version: v1
summary: Versioned HTTP, event, health and worker contract references from repository schemas.
order: 55
---

# REST and worker API reference

The versioned REST contract is `contracts/openapi/openconnect.v1.yaml`. The generated build artifact copies that contract and the JSON Schemas into the portal's versioned generated contract directory.

## Implemented gateway routes

The current ASP.NET Core host maps authenticated task, session, event replay, capability and component routes under `/openconnect/v1`. Voice, Navigator and MCP route groups are optional and report their actual readiness.

## Contract rules

Requests use versioned schemas and stable error codes. Task creation requires an idempotency key. Tenant and principal scope come from authenticated host claims, not request JSON. Raw sensitive content must not be placed in NATS messages.

Worker health is split between liveness and readiness. A live process is not proof that a model-backed capability is installed or usable.
