# Task 005C same-host Todo gateway requirements

**Status:** BLOCKED pending required host execution contracts.

- **TODO-GW-REQ-001:** Preserve external-gateway mode and its current `GATEWAY_URL` behavior.
- **TODO-GW-REQ-002:** Same-host mode must compose `MapOpenConnect()` in the existing Blazor host without a second application or execution engine.
- **TODO-GW-REQ-003:** Same-host mode must use trusted authentication and canonical `tenant_id`/principal claims; development headers must never be accepted as production authority.
- **TODO-GW-REQ-004:** Todo tools must reuse the existing `ITodoStore`; no second store or database may be introduced.
- **TODO-GW-REQ-005:** Real task execution requires a registered `IHostTaskAgentExecutionFactory`, `IHostTaskExecutionAuthorizer`, `IChatClient`, MAF runner, persistence context and revocation callback. Missing any required contract must fail closed.
- **TODO-GW-REQ-006:** Same-host client credential forwarding must be explicit and must not forward arbitrary headers or silently derive tenant scope from request data.
- **TODO-GW-REQ-007:** Process-local Todo data must not be described as restart durable. SQLite currently persists OpenConnect records only.
- **TODO-GW-REQ-008:** Deterministic MAF tests and real-provider tests must be separate; fake model output is never live-provider evidence.

## Current disposition

Route-level composition is architecturally compatible, but the repository has no sample `IHostTaskAgentExecutionFactory`, no sample `IChatClient`/model registration, no Todo MAF tool registration, no production authentication handler, and no safe same-host client credential forwarding. Implementing the requested end-to-end mode now would require inventing authority and execution behavior, so the feature remains BLOCKED rather than exposing a misleading partially working mode.
