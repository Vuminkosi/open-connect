# Task 005B tasks

- [x] **005B-TASK-001 Audit** — Mapped the Blazor app, external worker gateway, auth headers, MAF execution registration, SQLite OpenConnect persistence and process-local Todo store.
- [x] **005B-TASK-002 Static Todo smoke** — Corrected and reran `samples/EndToEnd/cases/Run-Blazor.ps1`; HTTP 200/current UI marker passed and process/port/SQLite cleanup passed.
- [ ] **005B-TASK-003 Gateway Todo E2E** — BLOCKED because the existing gateway has no Todo tool/store registration and the Blazor app does not map gateway endpoints. No replacement gateway may be created under this task.
- [ ] **005B-TASK-004 Browser E2E** — NOT VERIFIED; no Playwright project or stable browser fixture exists.
- [ ] **005B-TASK-005 Accessibility** — NOT VERIFIED; axe-core and manual review are pending browser infrastructure.
- [x] **005B-TASK-006 Published consumers** — PASS for clean NuGet.org net8/net9/net10 core consumers; MAF registration is unavailable on net8 due published dependency constraints.
- [ ] **005B-TASK-007 Semantic contracts** — PARTIAL; OpenAPI references and JSON Schema parsing pass, full semantic and route coverage remain.
- [x] **005B-TASK-008 Evidence** — Evidence is recorded in developer-portal tasks and this spec.
- [ ] **005B-TASK-009 Review** — Independent security/accessibility/integration review remains NOT VERIFIED.
