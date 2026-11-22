# OpenConnect HTTP task contract

The task endpoints use the ASP.NET host and .NET client wire shape as the
canonical contract. A task request contains `clientTaskId`, an optional
`message`, and an optional existing `sessionId`. A task record contains `id`,
`tenantId`, `principalId`, and `state`. Identifiers are canonical nonzero ULIDs,
and states use the kebab-case `AgentRunState` values.

Earlier TypeScript definitions and the OpenAPI task schema described `input`,
`agentId`, `schemaVersion`, and `taskId`; these did not match the host and could
silently discard the message. The TypeScript client rejects the old request
shape. Consumers must migrate to `message` and `id`; task submission does not
currently provide an agent-selection field. The host's existing .NET wire
format is unchanged.

## Application-gateway route coverage

`openconnect.v1.yaml` describes the public HTTP operations called by
`OpenConnect.Client` and `@openconnect/client`. The host supplies authentication;
the bearer scheme describes token-based deployments. OpenAPI does not advertise
optional host/admin operations, internal worker APIs, or WebSocket message
contracts. The HTTP replay operation remains documented even when the same
path also accepts a WebSocket upgrade.

`host-route-exclusions.json` lists public, mapped host method/path pairs that are
intentionally outside the core SDK HTTP contract. Use an explicit reason per
pair, never a wildcard; `UPGRADE` is a route without an HTTP-method constraint
that serves a separately documented transport. Do not add internal or
development-only route names to these public docs. The Hosting route-parity
test checks the actual in-process endpoint data sources without starting a
server. It rejects unmatched host routes, phantom OpenAPI paths, stale or
unreasoned exclusions, and overlaps. Keep `paths:` in the document's existing
block format (two-space path keys, four-space method keys), which is the index
format read by the test.

The model-only `GET /capabilities` response is a host registry descriptor, not a
versioned worker capability envelope. The TypeScript client's existing
`Capability[]` return annotation differs from that wire shape; this document
records what the host sends and does not change client types.
