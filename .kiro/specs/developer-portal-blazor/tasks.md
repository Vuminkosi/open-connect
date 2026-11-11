# Developer portal on Blazor Web App tasks

- [x] **PORTAL-TASK-001 Requirements** — Reconciled portal requirements with framework requirements, worker E2E boundaries, public APIs and historical `OC2-D-REL-023/024/025` IDs.
- [x] **PORTAL-TASK-002 Design** — Accepted the .NET 10 static SSR/interactive-island architecture and preserved `docs/site` and `samples/Blazor` during migration. ADR-006 records the supersession.
- [x] **PORTAL-TASK-003 Implementation** — Created `site/OpenConnect.Docs` with static SSR routes, versioned Markdown front matter, safe rendering, navigation, metadata, health, sitemap and interactive local search.
- [ ] **PORTAL-TASK-004 Implementation** — Optional OpenConnect-powered read-only assistant wiring exists behind configuration, but a grounded assistant response/citation test is NOT VERIFIED.
- [ ] **PORTAL-TASK-005 API/docs consumers** — DocFX 2.81.0 metadata generation and `/api` rendering are implemented. Clean NuGet consumers for net8/net9/net10 pass against NuGet.org package version 1.2.1, but net8 intentionally does not load `OpenConnect.Agents` because its published dependency graph requires Microsoft.Extensions.DependencyInjection >=10.0.12. Published-package API artifact comparison and CI artifact upload remain pending.
- [x] **PORTAL-TASK-006 Todo maintenance** — Created the separate Todo maintenance spec and added an accurate Todo tutorial without changing `samples/Blazor` source.
- [ ] **PORTAL-TASK-007 Tests** — Added bUnit/HTML-safety tests and InMemoryTodoStore CRUD regression tests. Playwright, accessibility automation, gateway round trip, persistence/event replay and worker-absence tests remain NOT VERIFIED.
- [x] **PORTAL-TASK-008 CI** — Extended the documentation workflow with serialized solution build, DocFX/contract generation, content safety validation, portal component tests, published consumers and runnable snippet compilation. Browser, accessibility and gateway jobs remain pending.
- [x] **PORTAL-TASK-009 Evidence** — Recorded actual baseline, package-consumer, snippet, portal/API and contract commands below. Unrun checks remain explicitly marked.
- [ ] **PORTAL-TASK-010 Review** — Final independent portal security/accessibility/API review remains NOT VERIFIED.

## Evidence ledger

- Baseline source revision: `96b0b1d`.
- Locked solution restore: `dotnet restore OpenConnect.slnx --locked-mode` — PASS.
- Serialized full Release build: `dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror -m:1` — PASS before and after portal test integration.
- Full current solution tests: `dotnet test OpenConnect.slnx -c Release --no-build --no-restore --filter "Category!=NatsLive" -m:1` — PASS, 1,437 total, 1,436 passed, 0 failed, 1 skipped.
- Published consumers: `./scripts/verify-published-consumers.ps1` — PASS for clean NuGet.org consumers on net8.0, net9.0 and net10.0 using OpenConnect package version 1.2.1. The net8.0 result explicitly records MAF agent package unavailability because `OpenConnect.Agents` 1.2.1 requires Microsoft.Extensions.DependencyInjection >=10.0.12; the net9/net10 consumers compile and register the published agent API.
- Runnable snippets: `./scripts/verify-doc-snippets.ps1` — PASS, one `csharp runnable` snippet compiled against published `OpenConnect.Core` 1.2.1.
- DocFX generation: `./scripts/generate-docs-api.ps1 -Mode all` — PASS; 21 source projects, generated API YAML and OpenAPI/JSON Schema artifacts under ignored `site/OpenConnect.Docs/generated/`.
- Contract validation: `./scripts/validate-docs-contracts.ps1` — PASS; OpenAPI references resolve and JSON Schemas parse.
- Content validation: `./scripts/validate-docs-content.ps1` — PASS, 12 files and 12 unique routes.
- Portal bUnit/Todo tests: `dotnet test site/OpenConnect.Docs.Tests/OpenConnect.Docs.Tests.csproj -c Release --no-build --no-restore` — PASS, 4 tests.
- Portal static HTTP smoke: `/health/live`, `/health/ready`, `/`, `/docs`, versioned docs, `/api` and `/sitemap.xml` — PASS, HTTP 200; generated API metadata was packaged and surfaced by `/api`.
- NOT VERIFIED: live assistant/model, grounded citations, gateway authorization round trip, Playwright, automated accessibility, full REST semantic validation, container deployment, Navigator, voice and production deployment.
