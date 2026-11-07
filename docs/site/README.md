# OpenConnect docs site (SITE-001, Phases 1–2)

Static Astro Starlight frontend in `docs/site`. **Static only** — usable without any backend, no provider credentials, no VoiceDesk.

- Landing messaging: “Build AI applications. Not infrastructure.”
- Navigation covers Introduction, Getting Started, Architecture, AI Firewall, Agents, API Reference (stubs).
- Search: Starlight built-in Pagefind, generated at build time, works offline.
- Architecture diagrams: `Mermaid.astro` renders source as readable text without JS and progressively enhances with the locally bundled `mermaid` package (no CDN).
- Interactive MDX stubs: `ConfigExample`, `CliCommand`, `StatusPill` — display only, no code execution.

## Commands

```bash
cd docs/site
npm install
npm run build     # must succeed with no credentials configured
npm run check-links
```

## Deferred to later phases

- Phase 3: DocFX .NET API generation, OpenAPI integration, metadata-to-MDX pipeline.
- Phase 4: optional docs assistant via existing OpenConnect gateway/firewall/agents.
- Phases 5–6: CI workflows, security hardening, deployment guides, full link/accessibility validation.
