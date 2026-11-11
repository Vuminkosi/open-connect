# ADR-007: Todo same-host gateway feasibility boundary

**Status:** Accepted with implementation gate  
**Date:** 2026-11-11  
**Related:** ADR-006, Task 005C

## Decision

The existing Blazor Todo application may compose `MapOpenConnect()` in a future optional same-host mode because the route prefix `/openconnect/v1` is disjoint from Blazor routes and the mapping API is idempotent.

No same-host mode is enabled by this decision. Enabling it requires a complete sample-owned host execution composition: trusted authentication, canonical tenant/principal claims, task authorizer, `IHostTaskAgentExecutionFactory`, model provider, native MAF runner, persistence context, revocation check, explicit Todo tools and safe same-host client authentication propagation.

## Rationale

The current Blazor process has the Todo store and UI but no gateway mapping, production authentication, MAF provider, host execution factory or Todo MAF tools. The existing worker gateway has task execution and synthetic test authentication but no Todo store or Todo tools. Joining them without implementing those contracts would create a misleading partial gateway and could broaden authority or falsely claim MAF execution.

## Consequences

- External gateway mode remains the default and unchanged.
- The Todo store remains `InMemoryTodoStore`; no persistence migration is introduced.
- A deterministic fake model may only support explicitly labeled integration tests, never live-provider acceptance.
- A future implementation must fail closed when any required execution/auth/provider contract is absent.
- The current Task 005C gateway-backed Todo E2E acceptance is BLOCKED, not PASS.
