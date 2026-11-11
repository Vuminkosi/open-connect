# Developer portal on Blazor Web App tasks

- [x] **PORTAL-TASK-001 Requirements** — Reconciled portal requirements with framework requirements, worker E2E boundaries, public APIs and historical `OC2-D-REL-023/024/025` IDs.
- [x] **PORTAL-TASK-002 Design** — Accepted the .NET 10 static SSR/interactive-island architecture and preserved `docs/site` and `samples/Blazor` during migration. ADR-006 records the supersession.
- [x] **PORTAL-TASK-003 Implementation** — Created `site/OpenConnect.Docs` with static SSR routes, versioned Markdown front matter, safe rendering, navigation, metadata, health, sitemap and interactive local search.
- [ ] **PORTAL-TASK-004 Implementation** — Optional OpenConnect-powered read-only assistant wiring exists behind configuration, but a grounded assistant response/citation test is NOT VERIFIED.
- [x] **PORTAL-TASK-005 API docs** — Pinned DocFX 2.81.0, added `docfx.json`, generated metadata from 21 source projects, copied the OpenAPI/JSON Schema contracts into generated artifacts, and added a portal `/api` route. Published-package reference rendering and CI artifact review remain pending.
- [x] **PORTAL-TASK-006 Todo maintenance** — Created the separate Todo maintenance spec and added an accurate Todo tutorial without changing `samples/Blazor` source.
- [ ] **PORTAL-TASK-007 Tests** — Added bUnit/HTML-safety tests and InMemoryTodoStore CRUD regression tests. Playwright, accessibility automation, gateway round trip, persistence/event replay and worker-absence tests remain NOT VERIFIED.
- [x] **PORTAL-TASK-008 CI** — Extended the documentation workflow with serialized solution build, DocFX/contract generation, content safety validation and portal component tests. Browser and published-package gates remain pending.
- [x] **PORTAL-TASK-009 Evidence** — Recorded actual baseline and portal/API commands below. Unrun checks remain explicitly marked.
- [ ] **PORTAL-TASK-010 Review** — Final independent portal security/accessibility/API review remains NOT VERIFIED.

## Evidence ledger

- Baseline source revision: `96b0b1d`.
- Locked solution restore: `dotnet restore OpenConnect.slnx --locked-mode` — PASS.
- Serialized full Release build: `dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror -m:1` — PASS, 16.9 seconds before portal test project addition; repeated after addition — PASS, 8.4 seconds.
- Baseline non-live solution tests: `dotnet test OpenConnect.slnx -c Release --no-build --filter "Category!=NatsLive" --no-restore -m:1` — PASS, 1,433 total, 1,432 passed, 0 failed, 1 skipped, 28.3 seconds. The two named failures from the task were not present in repository evidence and were not reproduced.
- DocFX tool availability: `dotnet tool search docfx --detail` — PASS; pinned `docfx` 2.81.0 in `dotnet-tools.json`.
- DocFX generation: `./scripts/generate-docs-api.ps1 -Mode all` — PASS; 21 source projects, generated API YAML and contract artifacts under ignored `site/OpenConnect.Docs/generated/`.
- Content validation: `./scripts/validate-docs-content.ps1` — PASS, 12 files and 12 unique routes.
- Portal bUnit/Todo tests: `dotnet test site/OpenConnect.Docs.Tests/OpenConnect.Docs.Tests.csproj -c Release --no-build --no-restore` — PASS, 4 tests.
- Portal static HTTP smoke from the previous foundation: `/health/live`, `/health/ready`, `/`, `/docs`, versioned docs and `/sitemap.xml` — PASS, HTTP 200.
- NOT VERIFIED: live assistant/model, grounded citations, gateway authorization round trip, Playwright, automated accessibility, full REST schema semantic validation, published-package consumers, container deployment, Navigator, voice and production deployment.
