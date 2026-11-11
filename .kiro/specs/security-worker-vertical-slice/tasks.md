# Security worker vertical slice tasks

- [ ] **SEC-SLICE-TASK-001 Requirements** — Confirm the rules-only contract and negative cases against `workers-requirements.md` and `workers-samples-e2e-spec.md`.
- [ ] **SEC-SLICE-TASK-002 Design** — Review the private worker boundary, fail-closed behavior and evidence redaction.
- [ ] **SEC-SLICE-TASK-003 Implementation** — Normalize line endings for the shared/security shell harness so Linux `sh` executes the scripts.
- [ ] **SEC-SLICE-TASK-004 Tests** — Run T0 and T1 contract tests with SQLite provider selection and no network/model provisioning.
- [ ] **SEC-SLICE-TASK-005 Tests** — Run T2/T3 in an isolated Docker Compose project and capture readiness, scan, 401 and 503 results.
- [ ] **SEC-SLICE-TASK-006 Evidence** — Record source revision, image digest, exact commands and sanitized artifacts.
- [ ] **SEC-SLICE-TASK-007 Review** — Review the diff for privacy, tenant boundary, secret handling and destructive cleanup.
