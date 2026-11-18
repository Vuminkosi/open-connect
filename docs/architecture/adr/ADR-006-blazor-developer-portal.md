# ADR-006: ASP.NET Core Blazor developer portal

**Status:** Accepted
**Date:** 2026-11-11
**Supersedes implementation direction:** Astro/Starlight hosting for `OC2-D-REL-023`, `OC2-D-REL-024`, `OC2-D-REL-025` and related SITE obligations.

## Decision

Build the official OpenConnect developer portal as an ASP.NET Core .NET 10 Blazor Web App. Static SSR is the default. Component-level Interactive Server rendering is used only for search, theme controls and explicitly enabled interactive integrations. DocFX remains a build-time API metadata generator; it is not the website host.

The existing `docs/site` Astro/Starlight project is preserved during migration as historical/static source material until a separately reviewed content migration removes duplication. The existing `samples/Blazor` Todo application remains an independent maintained consumer and is not rewritten or moved by this decision.

## Rationale

The repository's product is .NET-first and the existing framework exposes public .NET client, Blazor component, privacy and core registration APIs. A .NET portal can compile against those APIs, share the repository's TFM/package gates, and demonstrate static documentation availability without requiring Node, a model provider, workers, a database or an interactive circuit.

## Consequences

- Portal hosting, deployment and health checks use ASP.NET Core/.NET 10.
- Static documentation remains available when OpenConnect AI infrastructure is absent.
- Existing Astro/Starlight assets and historical acceptance IDs must be preserved and reconciled, not silently deleted.
- Optional assistant integration must use existing OpenConnect APIs and read-only retrieval policy. No second agent engine, session manager, document protocol or MiniComposer implementation is permitted.
- `OC2-D-REL-023/024/025` remain behavior obligations, but their Astro/Starlight implementation is superseded. New evidence is recorded under the developer-portal specification.

## Alternatives rejected

- **Continue Astro/Starlight as official host:** rejected because the requested official platform must be ASP.NET Core/.NET 10 and must demonstrate OpenConnect's .NET integration.
- **React/Node documentation application:** rejected because it introduces a separate website runtime and duplicates the .NET consumer path.
- **DocFX static website:** rejected because DocFX is retained as API metadata generation only, not the application host.
