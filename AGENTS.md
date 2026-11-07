# OpenConnect Development Guide for AI Agents

This document provides guidance for AI agents (including Kiro, Codex, and similar tools) working on the OpenConnect codebase.

## Project Overview

OpenConnect is an open-source, .NET-first, privacy-first, model-agnostic AI application framework. It provides reusable libraries, secure defaults, and modular components for building AI-powered applications.

## Key Principles

### Privacy-First

- Privacy firewall enabled by default
- Security boundary in .NET host, never delegated to workers
- Fail-closed behavior: when in doubt, deny
- No automatic model downloads or external data transmission

### Developer-First

- Simple DI registration: `services.AddOpenConnect()`
- Strongly typed contracts across .NET, Python, and TypeScript
- Clear error messages and documentation

### Framework, Not Platform

- Layered on Microsoft Agent Framework (MAF)
- Provides application infrastructure, not agent orchestration
- Extensible through standard .NET patterns

## Architecture

### Layered Structure

1. **Client Layer**: Blazor components, TypeScript SDK, .NET client
2. **Gateway Layer**: ASP.NET Core HTTP/WebSocket endpoints
3. **Agent Layer**: MAF integration for agent execution
4. **Privacy Layer**: Firewall, policy engine, tokenization, egress gates
5. **Runtime Layer**: Agent registry, tool broker, capability router
6. **Transport Layer**: NATS JetStream
7. **Persistence Layer**: PostgreSQL
8. **Worker Layer**: Optional Python services

### Package Boundaries

- **OpenConnect.Abstractions**: Core interfaces, IDs, DTOs - NO external dependencies
- **OpenConnect.Core**: Implementation, DI registration, configuration
- **OpenConnect.Agents**: MAF adapters, agent lifecycle
- **OpenConnect.Privacy**: Firewall, policy, vault, gates
- **OpenConnect.Sessions**: Session state management
- **OpenConnect.Tools**: Tool discovery and authorization
- **OpenConnect.Transport.Nats**: JetStream integration
- **OpenConnect.Persistence.Postgres**: EF Core, migrations
- **OpenConnect.Hosting.AspNetCore**: HTTP/WS endpoints

## Coding Standards

### C#/.NET

- Target Framework: .NET 8 LTS
- Language Version: C# 12
- Nullable Reference Types: Enabled
- Implicit Usings: Enabled
- Treat Warnings as Errors: Release builds only

#### Naming Conventions

- **Namespaces**: `OpenConnect.{Layer}.{Component}`
- **Public types**: PascalCase
- **Private fields**: `_camelCase`
- **Constants**: PascalCase
- **Locals/parameters**: camelCase

#### DI Registration Pattern

```csharp
public static class OpenConnectServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnect(
        this IServiceCollection services,
        Action<OpenConnectOptions>? configure = null)
    {
        // Register options with defaults
        services.Configure<OpenConnectOptions>(options =>
        {
            options.Privacy.Enabled = true; // Default-on
            configure?.Invoke(options);
        });

        // Validate on start
        services.AddOptions<OpenConnectOptions>()
            .ValidateOnStart();

        // Register services
        services.TryAddSingleton<IAgentRegistry, AgentRegistry>();

        return services;
    }
}
```

### Python

- Version: Python 3.12+
- Package Manager: uv (locked)
- Type Hints: Required
- Linting: Ruff
- Testing: pytest

### TypeScript

- Node Version: Node 24+
- Package Manager: npm (lockfile)
- Module Type: ESM
- Strict Mode: Enabled
- Testing: Vitest

## Dependency Management

### .NET

- Central package management: `Directory.Packages.props`
- SDK version: `global.json`
- Lockfile: `packages.lock.json` (must be committed)
- Restore: `dotnet restore --locked-mode`

### Python

- Lockfile: `uv.lock` (must be committed)
- Sync: `uv sync --locked`

### npm

- Lockfile: `package-lock.json` (must be committed)
- Install: `npm ci`

## Build Commands

```bash
# .NET
dotnet restore OpenConnect.slnx --locked-mode
dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror
dotnet test OpenConnect.slnx -c Release --no-build

# Python (in workers/{name})
uv sync --locked
uv run ruff check .
uv run mypy src
uv run pytest -q

# TypeScript (in packages/client-ts)
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

## Testing Requirements

### Required Test Categories

1. **Unit Tests**: Fast, isolated, no database/network
2. **Integration Tests**: Testcontainers for PostgreSQL, NATS
3. **Contract Tests**: JSON Schema fixtures, cross-language parity
4. **Security Tests**: Adversarial fixtures, authorization matrices
5. **Package Consumer Tests**: Install published packages, compile, run samples

### Test Coverage

- Core abstractions: 90%+
- Privacy firewall: 100% on critical paths
- Authorization: 100% on security boundaries

## Security Guidelines

### Never

- Store secrets in code
- Log sensitive data
- Send raw PII to NATS
- Trust model output for auth
- Allow cross-tenant access
- Grant permanent tokens
- Bypass privacy gates

### Always

- Validate input
- Authorize every tool call
- Encrypt token vault
- Filter by tenant_id
- Fail closed on errors
- Log audit events (without sensitive data)

## Privacy Firewall

### Default Configuration

- **Enabled**: true
- **Mode**: strict-local
- **OnFailure**: deny
- **Detectors**: rules (enabled), others (disabled by default)

### Detection Modes

- **strict-local**: Local detection only, sanitized external egress
- **local-only**: Local only, no remote calls
- **policy-controlled**: Policy-defined behavior
- **disabled**: No privacy enforcement (auth still required)

### Detector Independence

Each detector is independently configurable:

- `Rules`: Enabled by default (CPU-based, minimal overhead)
- `GlinerPii`: Disabled by default (semantic PII detection)
- `DebertaInjection`: Disabled by default (injection detection)
- `Qwen3Guard`: Disabled by default (safety classification)
- `PaddleOcr`: Disabled by default (image text extraction)
- `YuNet`: Disabled by default (face detection)

**Disabled = No model load, no download, no capability advertisement.**

## Error Handling

### Fail-Closed Behavior

- Missing detector → Block external egress
- Authorization failure → Deny
- Schema validation failure → Reject
- Expired authorization → Revoke

### Error Codes

Use stable, documented error codes:

- `task_not_found`
- `session_not_found`
- `unauthorized`
- `tool_not_allowed`
- `approval_required`
- `capability_unavailable`
- `privacy_blocked`
- `invalid_request`
- `conflict`
- `rate_limited`

## Git Workflow

- Never push directly to main/master
- Never use force push
- Never skip hooks (--no-verify) unless explicitly requested
- Preserve existing staged/unstaged work
- Prefer staging specific files over `git add .`

## Release Process

- Semantic Versioning
- OIDC trusted publishing (no API keys)
- Signed container images
- SBOM generation
- Package consumer verification

## References

- Architecture: `.kiro/steering/architecture.md`
- Security: `.kiro/steering/security.md`
- Engineering: `.kiro/steering/engineering.md`
- Product: `.kiro/steering/product.md`
- Implementation Tasks: `.kiro/specs/openconnect-core/tasks.md`
- Design: `.kiro/specs/openconnect-core/design.md`
- Requirements: `.kiro/specs/openconnect-core/requirements.md`
