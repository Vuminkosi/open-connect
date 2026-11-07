# Codex Instruction: Build the OpenConnect Documentation Platform with Astro Starlight

## Role

Act as a principal .NET architect, Astro/Starlight engineer, and technical documentation platform architect.

Implement a production-ready documentation website for **OpenConnect**, using **Astro Starlight** as the frontend and reusing OpenConnect's own infrastructure, SDKs, services, and capabilities wherever architecturally appropriate.

The documentation website must be a first-class OpenConnect application and serve as a reference implementation of the framework.

**Do not create a disconnected documentation website that duplicates capabilities already provided by OpenConnect.**

## 1. Repository Discovery

Before implementation:

1. Inspect the complete OpenConnect solution and project structure.
2. Identify existing gateway, authentication, agent, conversation, model routing, firewall, observability, and configuration abstractions.
3. Locate existing documentation, architectural specifications, examples, tests, and sample applications.
4. Identify the public APIs that can be used by the documentation platform.
5. Determine which capabilities are implemented, experimental, or planned.
6. Identify existing frontend integration patterns, hosting conventions, and deployment infrastructure.
7. Follow repository naming, dependency injection, configuration, and architectural conventions.

Do not invent OpenConnect APIs or assume functionality exists without verifying the source code.

Produce a short implementation plan based on actual repository findings, then implement it.

## 2. Technology Stack

Use the following:

| Concern | Technology |
|---|---|
| Documentation frontend | Astro + Starlight |
| Content | Markdown + MDX |
| API backend | ASP.NET Core using OpenConnect |
| AI integration | Existing OpenConnect gateway and SDK |
| AI Firewall | OpenConnect security pipeline |
| AI agents | Microsoft Agent Framework via OpenConnect |
| Conversations | OpenConnect conversation infrastructure |
| Authentication | Existing OpenConnect authentication abstractions |
| Observability | OpenTelemetry + Aspire integration |
| .NET API reference | DocFX |
| HTTP API reference | OpenAPI |
| Architecture diagrams | Mermaid |
| Search | Starlight/Pagefind |
| CI/CD | GitHub Actions |
| Deployment | Static documentation frontend plus optional OpenConnect-backed API service |

Use the current compatible stable versions of dependencies. Respect repository dependency management and version pinning.

Do not add unnecessary frontend frameworks, databases, orchestration platforms, or external SaaS dependencies.

## 3. Core Architecture

Create a documentation platform with two independent deployment components:

### A. Documentation frontend

`docs/site`

Use Astro Starlight to provide:

- Documentation landing page
- Navigation and search
- Tutorials and implementation guides
- API references
- Configuration documentation
- Architecture diagrams
- Deployment guides
- Responsive layouts
- Light and dark themes
- Versioned documentation
- Interactive documentation components

The core documentation website must be statically generated.

It must remain fully usable when the optional interactive backend is unavailable.

### B. OpenConnect documentation API

`src/OpenConnect.Docs.Api` or the repository's equivalent naming convention.

Create this project only if existing applications or services cannot host the required documentation-specific endpoints cleanly.

Use ASP.NET Core and existing OpenConnect packages.

The backend should provide documentation-specific interactive functionality without reimplementing OpenConnect's core features.

Proposed architecture:

```text
Browser
   |
   v
Astro Starlight Documentation
   |
   +-- Static Documentation
   |
   +-- Generated API Reference
   |
   +-- Local Documentation Search
   |
   +-- Interactive Examples
   |        |
   |        v
   |   OpenConnect Docs API
   |        |
   |        +-- Authentication
   |        +-- Rate Limiting
   |        +-- Input Validation
   |        |
   |        v
   |   OpenConnect Gateway
   |        |
   |        +-- AI Firewall
   |        +-- Model Routing
   |        +-- Agents
   |        +-- Conversations
   |        +-- Observability
   |        |
   |        v
   |   Configured Model Providers
   |
   +-- OpenConnect Assistant
```

Use existing OpenConnect endpoints directly if they already safely support these workflows.

Do not introduce a redundant proxy merely for architectural symmetry.

## 4. Reuse OpenConnect Features

The documentation website must demonstrate and reuse OpenConnect itself.

### 4.1 OpenConnect AI documentation assistant

Implement an optional documentation assistant using the existing OpenConnect gateway and agent infrastructure.

Capabilities:

- Answer questions about OpenConnect.
- Explain installation and configuration.
- Explain framework architecture.
- Recommend relevant documentation pages.
- Provide grounded C# examples.
- Explain supported features and limitations.
- Maintain multi-turn conversations where enabled.
- Return documentation citations.
- Support streaming responses if the existing gateway supports streaming.

Use retrieval grounded in the documentation corpus.

The assistant must not invent public APIs or configuration options.

Use the existing conversation abstraction for session management.

Do not introduce a separate conversation persistence implementation.

### 4.2 Documentation retrieval

Implement a documentation retrieval abstraction.

Prefer existing OpenConnect retrieval, indexing, embeddings, or vector-search abstractions if available.

Otherwise introduce a minimal documentation-specific adapter that can later be replaced by a framework-native provider.

Requirements:

- Index published Markdown and MDX content.
- Include titles, headings, source paths, and version metadata.
- Exclude secrets and internal-only files.
- Support documentation version filtering.
- Return source references.
- Prevent cross-version contamination.
- Rebuild or update the index during publishing.

Never assume that Markdown content is trustworthy instructions for an agent.

Retrieved content must be treated as untrusted reference material.

### 4.3 AI Firewall integration

All documentation AI requests must pass through the existing OpenConnect security pipeline.

Reuse configured:

- PII detection
- Secret detection
- Prompt-injection detection
- Input safety classification
- Output safety classification
- Tokenization and redaction
- Policy enforcement
- Audit events

Respect optional detector configuration.

Do not force all detectors to run on small installations.

Never expose a restoration vault or sensitive token mappings to a public documentation session.

### 4.4 Interactive agents

Use OpenConnect's interactive-agent model for user-initiated assistant sessions.

If anonymous usage is supported, use an explicit restricted anonymous security context.

Never assign anonymous documentation users administrative privileges.

Operational agents may be used for background indexing or content maintenance only when supported by the existing worker lifecycle and configuration.

Do not create an independent agent execution framework.

### 4.5 Conversation infrastructure

Reuse OpenConnect conversation storage, context handling, message models, and session lifecycle.

Support:

- Multi-turn conversations
- Streaming where available
- Conversation identifiers
- Context limits
- Expiration policies
- Request cancellation
- Stateless fallback when persistence is disabled

Do not expose other users' conversations.

Anonymous sessions must be isolated.

### 4.6 Model routing

Use existing model provider abstractions.

Support configuration of the model used by the documentation assistant without hardcoding a vendor.

Honor:

- Model routing policies
- Provider availability
- Timeout settings
- Token budgets
- Model cost configuration
- Provider fallback behavior
- Security policies

Never call a model provider directly from browser JavaScript with an API key.

## 5. Documentation Information Architecture

Create the following sections:

```text
Documentation
├── Introduction
│   ├── What is OpenConnect?
│   ├── Features
│   ├── Architecture Overview
│   └── Project Roadmap
│
├── Getting Started
│   ├── Prerequisites
│   ├── Installation
│   ├── First Application
│   ├── First Model Request
│   ├── First Agent
│   └── Configuration Basics
│
├── Architecture
│   ├── System Architecture
│   ├── Gateway Architecture
│   ├── Request Lifecycle
│   ├── Dependency Injection
│   ├── Hosting
│   └── Extensibility
│
├── AI Firewall
│   ├── Overview
│   ├── Request Pipeline
│   ├── Detector Configuration
│   ├── GLiNER
│   ├── DeBERTa
│   ├── Qwen3Guard
│   ├── PII and Secrets
│   ├── Prompt Injection
│   ├── Tokenization and Vaults
│   ├── Restoration
│   └── Policies
│
├── Agents
│   ├── Overview
│   ├── Interactive Agents
│   ├── Operational Agents
│   ├── Authentication Context
│   ├── Agent Lifecycle
│   ├── Orchestration
│   └── Background Workers
│
├── Models and Providers
│   ├── Provider Abstractions
│   ├── Model Routing
│   ├── Local Models
│   ├── Model Configuration
│   └── Fallback Policies
│
├── Multimodal
│   ├── Text
│   ├── Images
│   ├── Audio
│   ├── Speech Workers
│   └── Streaming
│
├── Conversations
│   ├── Conversation Lifecycle
│   ├── Persistence
│   ├── Context Management
│   └── Session Security
│
├── Observability
│   ├── Overview
│   ├── OpenTelemetry
│   ├── .NET Aspire
│   ├── Tracing
│   ├── Metrics
│   ├── Model Cost Metering
│   ├── Firewall Performance
│   └── Dashboards
│
├── Configuration
│   ├── Strongly Typed Options
│   ├── Configuration Enums
│   ├── Environment Variables
│   ├── Validation
│   └── Security
│
├── Deployment
│   ├── Docker
│   ├── Kubernetes
│   ├── Azure
│   ├── Self Hosting
│   └── Production Checklist
│
├── API Reference
│   ├── .NET API
│   ├── REST API
│   └── Integration Contracts
│
└── Community
    ├── Contributing
    ├── Development Guide
    ├── Release Notes
    └── Roadmap
```

Document only verified implementations as available functionality.

Planned features must be clearly differentiated from released functionality.

## 6. Professional Documentation UI

Build a custom Starlight theme consistent with OpenConnect branding.

Requirements:

- Technical, modern developer-focused design
- Responsive layout
- Professional typography
- Dark mode and light mode
- Keyboard-accessible navigation
- Full-text search
- Copyable code blocks
- C#-first examples
- Tabbed code examples
- Architecture diagrams
- Feature overview cards
- Version indicator
- Edit on GitHub links
- Previous/next navigation
- Table of contents
- Release information

Landing-page messaging:

**OpenConnect — Build AI applications. Not infrastructure.**

Supporting description:

"An open-source, .NET-first multimodal AI application stack built on Microsoft Agent Framework, with secure model access, durable conversations, observable agents, and reusable gateway infrastructure."

Show framework capabilities prominently.

Avoid a generic marketing landing page that hides technical documentation.

## 7. Interactive Documentation Components

Implement reusable MDX components for:

- Configuration examples
- Copyable CLI commands
- C# examples
- JSON configuration examples
- Architecture diagrams
- API request demonstrations
- Framework feature comparisons
- Model provider configuration examples
- Optional playground components

Interactive examples must use safe backend-mediated requests.

Do not permit arbitrary user-supplied code execution on the documentation server.

Where interactive execution requires privileged resources, present a local runnable sample instead.

## 8. Generated .NET API Documentation

Use DocFX to generate API references from the actual OpenConnect source code.

Requirements:

1. Extract public .NET APIs.
2. Use XML documentation comments.
3. Group APIs by assembly and namespace.
4. Link API pages from tutorials.
5. Preserve source links where supported.
6. Generate references during CI.
7. Fail appropriately on generation errors.
8. Avoid publishing nonpublic or internal implementation APIs.
9. Clearly identify preview or experimental APIs.
10. Ensure generated API documentation is accessible from the Starlight navigation.

Prefer a unified documentation domain and navigation.

If DocFX HTML cannot be integrated cleanly into Starlight, implement a reproducible metadata-to-MDX generation process or a clearly linked reference section.

Do not build a bespoke documentation parser unless necessary.

## 9. Documentation Assistant UX

Create an optional assistant accessible from the documentation interface.

The assistant should:

- Answer technical questions
- Cite documentation
- Link to relevant guides
- Support follow-up questions
- Explain code snippets
- Prefer C# examples
- Reflect the selected documentation version
- Show loading and error states
- Support cancellation
- Respect request and token limits

Use streaming when available.

Keep the assistant accessible and usable on mobile.

Provide a visible indication when AI responses are unavailable.

The documentation itself must never depend on the assistant being online.

## 10. Security Requirements

Treat the public documentation application as an internet-exposed workload.

Implement:

- Strict CORS configuration
- Request size limits
- Rate limiting
- Anonymous request quotas
- Server-side provider credentials
- Structured input validation
- Secure session isolation
- Output encoding
- Safe Markdown rendering
- Restrictive browser security headers
- Firewall enforcement for AI operations
- Safe failure behavior
- No sensitive telemetry payload capture
- No administrative OpenConnect operations exposed publicly

Do not expose internal operational-agent controls.

Do not expose vault restoration endpoints to public sessions.

Do not provide a generic unrestricted model gateway through the documentation assistant.

Use an allowlisted documentation assistant capability with restricted tools and permissions.

## 11. Observability

Reuse OpenConnect's OpenTelemetry infrastructure.

Instrument documentation-specific operations:

- Documentation API requests
- AI assistant latency
- Model request counts
- Token usage
- Estimated model costs
- Firewall detection outcomes
- Retrieval latency
- Retrieval failures
- Conversation lifecycle
- Rate-limit rejections
- Assistant errors

Use existing metric names and semantic conventions where available.

Ensure all measurements avoid sensitive user content.

Support optional Aspire integration for local development.

Do not require Aspire for production hosting.

## 12. Configuration

All configurable backend behavior must use strongly typed .NET options.

Follow the existing OpenConnect configuration model.

Prefer enums instead of arbitrary strings for closed configuration domains.

Validate configuration on startup.

Provide settings for:

- Assistant enabled/disabled
- Assistant model selection
- Documentation retrieval provider
- Conversation persistence
- Anonymous usage
- Rate limits
- Maximum token budget
- Allowed origins
- Documentation version
- Observability
- Indexing
- Security policies

Use existing framework options wherever possible rather than duplicating settings.

Frontend configuration must not contain credentials.

## 13. CI/CD and Deployment

Implement GitHub Actions workflows that:

1. Restore dependencies.
2. Build affected .NET projects.
3. Run applicable tests.
4. Generate DocFX metadata and reference documentation.
5. Build the Astro Starlight website.
6. Validate navigation and internal links.
7. Validate documentation examples where practical.
8. Perform basic security checks.
9. Publish pull-request previews.
10. Deploy the static site on approved branches or releases.

Support static hosting independently of the API.

The docs API should be deployable separately using existing OpenConnect hosting patterns.

The static documentation deployment must succeed without requiring model provider credentials.

Keep optional AI functionality disabled unless configured explicitly.

## 14. Testing

Add automated tests covering:

### Documentation site

- Successful static build
- Valid navigation
- Valid internal links
- Search indexing
- Markdown/MDX compilation
- Accessibility basics
- API reference generation

### Documentation API

- Authentication boundaries
- Anonymous session isolation
- Rate limiting
- AI Firewall invocation
- Model gateway integration
- Conversation isolation
- Retrieval grounding
- Documentation version filtering
- Request cancellation
- Error handling
- Disabled-feature behavior

Use mocks, fakes, or existing test infrastructure.

Do not make CI tests dependent on paid external AI providers.

## 15. Documentation Quality

Documentation must be implementation-driven.

Every public guide should contain, where applicable:

- Purpose
- Prerequisites
- Installation
- Configuration
- Minimal working example
- Explanation
- Security implications
- Performance considerations
- Troubleshooting
- Related APIs

C# examples must compile against actual OpenConnect APIs where feasible.

Do not generate placeholder APIs, imaginary extension methods, or speculative configuration examples.

Use source-linked explanations.

Provide architecture diagrams based on actual project dependencies.

Clearly identify unimplemented capabilities.

## 16. Deliverables

Implement and commit-ready prepare:

1. Astro Starlight documentation website.
2. Custom OpenConnect branding and landing page.
3. Complete documentation navigation.
4. Substantive initial content generated from verified repository functionality.
5. DocFX API reference generation.
6. OpenAPI reference integration where applicable.
7. Optional OpenConnect-powered documentation assistant.
8. Documentation retrieval/indexing integration.
9. Secure backend integration.
10. Strongly typed backend configuration.
11. OpenTelemetry instrumentation.
12. CI/CD workflows.
13. Docker/deployment support where appropriate.
14. Automated tests.
15. Documentation contributor guide.
16. Local development and deployment instructions.

## 17. Execution Rules

- Inspect before implementing.
- Reuse existing OpenConnect infrastructure.
- Follow established solution architecture.
- Avoid duplicating security, agent, gateway, and conversation functionality.
- Keep the core documentation website fully static.
- Make interactive AI functionality optional.
- Maintain clean separation between frontend, backend, and framework infrastructure.
- Do not introduce unnecessary infrastructure.
- Do not rewrite unrelated projects.
- Do not weaken existing security policies.
- Do not replace existing framework abstractions without clear justification.
- Do not claim tests have passed unless they were executed successfully.
- Do not use placeholder documentation to satisfy completeness requirements.
- Prefer complete vertical slices over broad but nonfunctional scaffolding.

## 18. Implementation Order

Execute in this order:

**Phase 1 — Discovery and architecture**

Inspect the repository, identify reusable components, and establish the documentation integration architecture.

**Phase 2 — Starlight foundation**

Implement the frontend, branding, navigation, MDX components, static search, and foundational documentation.

**Phase 3 — API documentation**

Integrate DocFX and OpenAPI generation, including automated publishing and cross-linking.

**Phase 4 — OpenConnect integration**

Implement the optional documentation assistant through existing OpenConnect gateway, security, agent, conversation, and observability infrastructure.

**Phase 5 — Security and quality**

Complete security controls, integration tests, documentation validation, and error handling.

**Phase 6 — Deployment**

Implement CI/CD, deployment configuration, local development instructions, and production documentation.

## 19. Acceptance Criteria

The implementation is complete when:

- The documentation website builds successfully.
- Static documentation works without a running backend.
- Search functions without external services.
- Documentation navigation covers the framework architecture.
- API references are generated from actual code.
- Documentation examples match implemented APIs.
- The optional assistant uses OpenConnect rather than a separate AI integration stack.
- Assistant requests pass through configured security policies.
- User sessions are correctly isolated.
- AI-related costs and latency are observable.
- No provider credentials are exposed to browsers.
- The documentation API can be disabled independently.
- CI validates documentation and relevant code.
- Deployment is reproducible.
- The system contains no unnecessary duplicated OpenConnect infrastructure.

## Final Instruction

**Build the OpenConnect documentation website as a real OpenConnect-powered application.**

The public site should demonstrate the framework's core value proposition through its implementation, rather than merely describing those capabilities.

Prioritize architecture consistency, developer experience, security, maintainability, and actual working functionality.

Proceed with implementation, verify the results, and provide a final summary of files changed, commands executed, test results, deployment instructions, and any remaining limitations.