# Task 005C/005D tasks

- [x] **005C-TASK-001 Audit** — Captured Blazor entrypoint, Todo components/store, external gateway, auth claims, MAF execution adapter and SQLite OpenConnect persistence.
- [x] **005C-TASK-002 Architecture** — Confirmed route mapping is safe and documented the missing execution contracts.
- [x] **005C-TASK-003 ExternalGateway configuration** — Root Compose preserves `todo-web-app -> gateway-host` and Meta cloud inference configuration.
- [x] **005C-TASK-004 External host execution** — Added Meta-compatible `IChatClient`, task authorizer and native MAF-backed Todo task executor to the existing gateway host.
- [x] **005C-TASK-005 Todo tools** — Added governed `todo.list`, `todo.create`, `todo.update`, `todo.complete` and `todo.delete` AIFunction tools using the existing Todo service API and store. Mutation availability is controlled by trusted `TODO_ALLOW_MUTATIONS` configuration.
- [x] **005C-TASK-006 Internal Todo transport** — Added authenticated, tenant-scoped internal Todo API to the existing Blazor host; no second store or database was added.
- [x] **005D-TASK-007 Live ExternalGateway E2E** — `scripts/run-live-todo-e2e.ps1` passed with live Meta task completion, verified store mutation, 401 unauthenticated denial and 401 cross-tenant internal denial.
- [ ] **005C-TASK-008 SameHostGateway** — Not implemented yet; external mode remains the default and verified path.
- [ ] **005D-TASK-009 Approval/revocation** — Static trusted mutation policy is present; interactive approval receipts and revocation tests remain pending.
- [ ] **005D-TASK-010 WebSocket/replay/restart** — Not yet verified for the live Todo scenario.
- [ ] **005D-TASK-011 Voice** — Meta transcription configuration is present in Compose, but genuine audio transcription is not yet verified.
- [x] **005D-TASK-012 Regression/evidence** — Serialized build and full non-live suite remain green; live evidence is recorded in `docs/evidence/task005c.md`.
