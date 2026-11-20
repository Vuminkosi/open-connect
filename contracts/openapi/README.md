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
