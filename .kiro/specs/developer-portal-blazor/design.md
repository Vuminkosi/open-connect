# Developer portal on Blazor Web App design

## Context map

| Area | Current path | Disposition |
|---|---|---|
| Existing static docs | `docs/site/` | Preserve during migration; reuse content/assets incrementally, do not delete. |
| New portal host | `site/OpenConnect.Docs/` | .NET 10 Blazor Web App, static SSR default. |
| Existing Todo consumer | `samples/Blazor/` | Preserve source, behavior, tests and worker/gateway assumptions. |
| Public client API | `src/OpenConnect.Client/` | Use `IOpenConnectClient`, `AddOpenConnectClient`; no invented API. |
| Blazor integration | `src/OpenConnect.Components.Blazor/` | Use `AddOpenConnectComponents`, `AddOpenConnectComposer`, `OpenConnectProvider` and `MiniComposer` only behind an explicit assistant/gateway feature. |
| Framework composition | `src/OpenConnect.Core/` | Use `AddOpenConnect`; never map a gateway or create a second executor in the portal. |
| API source | `src/`, `contracts/openapi/` | DocFX/build-time generation consumes compiled public assets and OpenAPI. |
| Portal verification | new `site/OpenConnect.Docs.Tests/` or existing test infrastructure | Add focused content, routing, security and component coverage. |

## Hosting and dependency model

- `Program.cs` configures Razor Components and Interactive Server services but does not make the whole site interactive.
- Static content pages are ordinary Razor components without `@rendermode`.
- Search and theme controls are component-level `InteractiveServer` islands.
- Static content services use singleton immutable indexes and no external store.
- `AddOpenConnect()` is safe composition for portal-owned optional features and privacy defaults. It does not launch infrastructure.
- The optional assistant is configured only when `OpenConnect:Assistant:Enabled=true` and a valid gateway address is supplied. It uses the existing client/component registrations and has no write tools.
- Portal readiness means content index and static assets are available. Assistant/gateway readiness is a separate status and never makes `/docs/*` unavailable.

## Content pipeline

Markdown files use front matter:

```yaml
---
title: First application
version: v1
summary: Build a first OpenConnect application.
order: 10
---
```

The portal validates required fields, rejects duplicate route/version keys, sanitizes HTML blocks and allows only `https`, `http` where configured, and local relative links. The first renderer supports headings, paragraphs, fenced code blocks, unordered lists and links. DocFX and OpenAPI generation are separate build-time stages and emit generated metadata/content into `site/OpenConnect.Docs/generated/`.

## Security

- No secrets or model credentials are embedded in static assets.
- Search indexes contain documentation text only.
- Assistant citations are generated from the selected version's content index.
- Assistant tools are read-only retrieval functions; no filesystem, shell, MCP, mutation, browser or arbitrary component registration is exposed.
- The assistant failure state is rendered as unavailable and leaves static navigation intact.
- Any production assistant deployment must use HTTPS, authenticated gateway policy, privacy firewall and bounded budgets supplied by OpenConnect APIs.

## Todo maintenance boundary

The portal tutorial links to the existing Todo sample. It does not copy or reimplement Todo behavior. A later Todo maintenance spec may add evidence-backed fixes, but the portal migration cannot alter `samples/Blazor` implicitly.
