# Repository evidence snapshot

Source: GitHub connected repository inspected November 2026, `Vuminkosi/open-connect`, default branch `master` (recheck). `README.md`, `AGENTS.md`, project directories, tests, contracts, packages and `.github/workflows` inspected.

- .NET 8 LTS / C# 12 per AGENTS.md. Central package management, locked restores, public API declaration files and consumer compatibility checks.
- .NET library directories: Abstractions, Agents, Client, Components.Blazor, Components.Contracts, Core, Hosting.AspNetCore, LiveUI (Agents/Blazor/Compiler/Inference), Metering, Navigator (AspNetCore/Blazor/Operational/OperationalBrowser), Observability, Persistence.EFCore, Privacy, QdrantVactor, Tools, Transport.Nats, WorkerHosts.Operational and WorkerHosts.Outbox.
- npm packages: client-ts, composer, navigator, navigator-react. Python workers: classification, inference, kokoro, liveui-inference, mms-tts, navigator-playwright, nllb, operational-agents, outbox, realtime-voice, security, transcription.
- Contracts: JSON Schema, OpenAPI, Navigator and worker schemas; tests: Unit, Contracts, Browser. Samples: Blazor, EndToEnd, InMemory, Sqlite, SqlServer, PostgreSql, Navigator, operational examples.
- Core security: privacy firewall is default-on, strict-local, failure=deny; no automatic model downloads/egress; authorization decisions stay in .NET host, not workers. Multi-tenant boundaries, private data/PII, tool authorization and vault security are first-class.
- Previous README mentions deprecated `OpenConnect.Persistence.Postgres` while actual tree has `OpenConnect.Persistence.EFCore`; source prevails over docs. Do not file as an issue without checking current source and existing tickets.
- GitHub metadata reported `allow_auto_merge=false`. A real merge policy requires GitHub settings/checks and authorized delivery; do not bypass.
- Repository has existing Dependabot PRs. Reconcile open/closed PRs and issue duplicates before filing or resolving.
