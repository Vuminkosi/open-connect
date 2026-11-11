# Developer portal on Blazor Web App requirements

**Status:** Approved implementation direction, evidence-gated.  
**Supersedes:** Astro/Starlight site-hosting implementation for `OC2-D-REL-023`, `OC2-D-REL-024`, `OC2-D-REL-025`; the historical IDs and behavioral obligations remain in scope.

## Product boundary

OpenConnect's official developer portal is an ASP.NET Core .NET 10 Blazor Web App. Static SSR is the default. Interactive Server is used only for bounded interactive islands such as search, theme controls and an optional documentation assistant. The portal is a framework consumer, not a replacement agent runtime or gateway.

## Requirements

- **PORTAL-REQ-001:** Static documentation routes render without a database, model provider, worker, interactive circuit or OpenConnect gateway.
- **PORTAL-REQ-002:** The portal uses versioned Markdown files with YAML front matter, a validated content index and safe route generation.
- **PORTAL-REQ-003:** Markdown rendering sanitizes raw HTML and unsafe links before output.
- **PORTAL-REQ-004:** The portal provides accessible responsive navigation, breadcrumbs, table of contents, dark/light theme controls and a 404 page.
- **PORTAL-REQ-005:** The portal provides local search over the content index without AI infrastructure.
- **PORTAL-REQ-006:** Pages emit canonical URL, Open Graph metadata, version labels and sitemap entries.
- **PORTAL-REQ-007:** API reference generation consumes actual compiled public assemblies/XML and OpenAPI contracts. Missing or invalid public references fail the generation check.
- **PORTAL-REQ-008:** The optional assistant is disabled by default, uses actual OpenConnect client/components when enabled, exposes read-only documentation retrieval only, and never gates static documentation.
- **PORTAL-REQ-009:** Assistant retrieval is version-scoped and cites exact portal routes. Tool, filesystem, MCP and mutation capabilities are not registered.
- **PORTAL-REQ-010:** The portal has separate liveness and readiness endpoints. Readiness reports static content readiness independently of optional AI readiness.
- **PORTAL-REQ-011:** The existing `samples/Blazor` Todo application remains independent and unchanged unless a separate maintenance task is justified by evidence.
- **PORTAL-REQ-012:** A tutorial documents running the existing Todo sample and labels its current process-local Todo store and gateway dependencies accurately.
- **PORTAL-REQ-013:** Portal and Todo are separate consumer applications in build and regression validation.
- **PORTAL-REQ-014:** CI validates restore, Release build, Markdown/front matter, links, API generation, snippets, bUnit/browser smoke, security/static assets and Todo regression without claiming unrun live worker/model behavior.

## Out of scope for the first vertical slice

DocFX full API rendering, live assistant model calls, Navigator/voice workers, durable portal sessions, production deployment, registry publishing, and Todo persistence changes.
