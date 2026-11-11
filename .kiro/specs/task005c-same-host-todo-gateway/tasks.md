# Task 005C tasks

- [x] **005C-TASK-001 Audit** — Inspected Blazor composition, Todo store/components, client, MapOpenConnect, auth claims, task execution adapter, MAF factory and SQLite persistence.
- [x] **005C-TASK-002 Architecture** — Confirmed route mapping is safe but same-host execution is incomplete without a factory/provider/authentication/tool boundary.
- [ ] **005C-TASK-003 Configuration** — Add strongly typed ExternalGateway/SameHostGateway mode only after the missing execution/auth contracts are approved.
- [ ] **005C-TASK-004 Host composition** — Register and map same-host gateway with fail-closed startup validation.
- [ ] **005C-TASK-005 Todo tools** — Register governed native MAF Todo tools through the existing store and authorization boundary.
- [ ] **005C-TASK-006 Same-host client auth** — Implement explicit safe credential propagation or a documented in-process transport.
- [ ] **005C-TASK-007 E2E** — Execute authenticated/unauthenticated, tenant, approval, event replay, restart and worker-absence tests.
- [x] **005C-TASK-008 Preservation** — External gateway mode and process-local Todo behavior remain unchanged; no duplicate application or store was created.
- [ ] **005C-TASK-009 Evidence/review** — Blocked pending implementation and independent review.
