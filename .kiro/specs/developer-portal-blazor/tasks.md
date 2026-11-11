# Developer portal on Blazor Web App tasks

- [x] **PORTAL-TASK-001 Requirements** — Reconciled portal requirements with framework requirements, worker E2E boundaries, public APIs and historical `OC2-D-REL-023/024/025` IDs.
- [x] **PORTAL-TASK-002 Design** — Accepted the .NET 10 static SSR/interactive-island architecture and preserved `docs/site` and `samples/Blazor` during migration. ADR-006 records the supersession.
- [x] **PORTAL-TASK-003 Implementation** — Created `site/OpenConnect.Docs` with static SSR routes, versioned Markdown front matter, safe rendering, navigation, metadata, health, sitemap and interactive local search.
- [ ] **PORTAL-TASK-004 Implementation** — Optional OpenConnect-powered read-only assistant wiring exists behind configuration, but a grounded assistant response/citation test is NOT VERIFIED.
- [ ] **PORTAL-TASK-005 API docs** — DocFX/OpenAPI build-time metadata generation and invalid-reference failure gate remain NOT VERIFIED.
- [x] **PORTAL-TASK-006 Todo maintenance** — Created the separate Todo maintenance spec and added an accurate Todo tutorial without changing `samples/Blazor` source.
- [ ] **PORTAL-TASK-007 Tests** — Content/security tests, bUnit, Playwright smoke/accessibility and independent Todo regression coverage remain NOT VERIFIED.
- [x] **PORTAL-TASK-008 CI** — Added an initial dedicated documentation workflow for locked restore, Release portal build, front matter checks, secret scan and Todo source build. Browser/API generation gates remain pending.
- [x] **PORTAL-TASK-009 Evidence** — Recorded actual local build and HTTP smoke commands below. Unrun checks remain explicitly marked.
- [ ] **PORTAL-TASK-010 Review** — Final independent portal security/accessibility/API review remains NOT VERIFIED.

## Evidence ledger

- Repository source revision before portal changes: `5415ab6`.
- Portal restore: `dotnet restore site/OpenConnect.Docs/OpenConnect.Docs.csproj --locked-mode` — PASS.
- Portal build: `dotnet build site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --no-restore -warnaserror` — PASS.
- Local HTTP smoke on .NET 10 process at `http://127.0.0.1:5187`: `/health/live`, `/health/ready`, `/`, `/docs`, `/docs/v1/getting-started`, `/sitemap.xml` — PASS, all HTTP 200; content and sitemap assertions passed.
- Todo source preservation: no `samples/Blazor` source paths changed in the portal slice.
- NOT VERIFIED: DocFX generation, published-package consumers, bUnit, Playwright, accessibility automation, container deployment, live assistant/model, Navigator, voice, worker E2E and production deployment.
