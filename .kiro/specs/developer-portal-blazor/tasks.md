# Developer portal on Blazor Web App tasks

- [ ] **PORTAL-TASK-001 Requirements** — Reconcile the portal requirements with framework requirements, worker E2E boundaries, public APIs and historical `OC2-D-REL-023/024/025` IDs.
- [ ] **PORTAL-TASK-002 Design** — Approve the .NET 10 static SSR/interactive-island architecture and preserve `docs/site` and `samples/Blazor` during migration.
- [ ] **PORTAL-TASK-003 Implementation** — Create `site/OpenConnect.Docs` with static SSR routes, versioned Markdown front matter, safe rendering, navigation, metadata, health and search.
- [ ] **PORTAL-TASK-004 Implementation** — Add optional OpenConnect-powered read-only assistant wiring using existing public client/components, disabled by default.
- [ ] **PORTAL-TASK-005 API docs** — Add DocFX/OpenAPI build-time metadata generation and fail on invalid API references or missing public symbols.
- [ ] **PORTAL-TASK-006 Todo maintenance** — Create the separate Todo maintenance spec and portal tutorial; do not change Todo behavior without evidence.
- [ ] **PORTAL-TASK-007 Tests** — Add content/front matter/link/security tests, bUnit tests, Playwright smoke/accessibility tests and independent Todo regression coverage.
- [ ] **PORTAL-TASK-008 CI** — Add dedicated documentation validation workflow and separate source-integration versus published-package consumer jobs.
- [ ] **PORTAL-TASK-009 Evidence** — Record exact command, runtime, package versions, artifacts, negative coverage and reviewer result for each task.
- [ ] **PORTAL-TASK-010 Review** — Review migration safety, static availability, assistant authorization/privacy, API drift and preserved Todo behavior.
