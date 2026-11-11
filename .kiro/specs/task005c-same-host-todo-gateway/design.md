# Task 005C same-host Todo gateway design

## Existing boundaries

`Samples.Blazor` owns the UI and process-local `InMemoryTodoStore`. `OpenConnect.Hosting.AspNetCore.MapOpenConnect()` owns the HTTP route surface and requires authenticated claims plus bounded execution services. The current sample worker host provides synthetic development authentication and MAF host registration, but it does not expose Todo services or a general host execution factory.

## Safe composition decision

Calling `MapOpenConnect()` in the Blazor host is route-safe because the OpenConnect routes are under `/openconnect/v1` and mapping is idempotent. It is not operationally sufficient. The host must also supply:

- production-capable authentication and authorization;
- canonical tenant/principal claims;
- `AddOpenConnectTaskExecution()`;
- `IHostTaskExecutionAuthorizer`;
- `IHostTaskAgentExecutionFactory`;
- `IChatClient` and model/provider policy;
- MAF conversation runner and native session persistence context;
- explicit tool admission/approval and revocation;
- safe same-host client authentication propagation.

None of those sample-specific contracts currently exists as a complete composition. The implementation must not fabricate them.

## Required next implementation slice

Build a sample-owned execution adapter only after its contracts are approved: one task-scoped factory creates a trusted `HostTaskAgentExecution`, binds the authenticated tenant/principal/session, creates native MAF tools from the existing Todo service, and uses the existing OpenConnect persistence context. A deterministic `IChatClient` is acceptable for a non-live integration test only; a real provider must be separately configured and labeled.
