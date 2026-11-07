---
inclusion: auto
name: openconnect-engineering
description: Coding standards, testing strategy, CI/CD, and development practices for OpenConnect
---

# OpenConnect Engineering Steering

## Language and Framework Conventions

### C#/.NET

- **Target Framework:** .NET 8 LTS
- **Language Version:** C# 12
- **Nullable Reference Types:** Enabled
- **Implicit Usings:** Enabled
- **Treat Warnings as Errors:** Release builds only
- **EditorConfig:** Enforced formatting

#### Naming Conventions

```csharp
// Namespaces
namespace OpenConnect.{Layer}.{Component}

// Public APIs
public interface IAgentRegistry { }
public class AgentRegistry : IAgentRegistry { }
public record struct SessionId(Ulid Value);
public enum AgentExecutionMode { Interactive, Operational }

// Private fields
private readonly IAgentRegistry _agentRegistry;
private readonly ILogger<AgentService> _logger;

// Constants
public const string DefaultAgentId = "lead";
private const int MaxRetries = 3;
```

#### Project Structure

```
OpenConnect.{Layer}/
├── Public interfaces and contracts at root
├── Internal/ for implementation details
├── Extensions/ for DI extension methods
└── Properties/ (AssemblyInfo)
```

#### Dependency Injection

```csharp
public static class OpenConnectServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnect(
        this IServiceCollection services,
        Action<OpenConnectOptions>? configure = null)
    {
        services.Configure<OpenConnectOptions>(options =>
        {
            options.Privacy.Enabled = true;
            configure?.Invoke(options);
        });
        
        services.AddOptions<OpenConnectOptions>()
            .ValidateOnStart();
        
        services.TryAddSingleton<IAgentRegistry, AgentRegistry>();
        
        return services;
    }
}
```

#### Async Patterns

```csharp
// Prefer async
public Task<TaskResponse> CreateTaskAsync(
    TaskRequest request,
    CancellationToken cancellationToken = default);

// ConfigureAwait false for library code
await _store.SaveAsync(entity)
    .ConfigureAwait(false);

// No .Result or .Wait()
```

### Python

- **Version:** Python 3.12+
- **Package Manager:** uv (locked)
- **Type Hints:** Required
- **Linting:** Ruff
- **Formatting:** Ruff format
- **Testing:** pytest

#### Project Structure

```
workers/{name}/
├── pyproject.toml
├── uv.lock
├── Dockerfile
├── src/openconnect_{name}/
│   ├── __init__.py
│   ├── app.py
│   ├── config.py
│   └── schemas.py
└── tests/
    ├── unit/
    └── contracts/
```

### TypeScript

- **Node Version:** Node 24+
- **Package Manager:** npm (lockfile)
- **Module Type:** ESM
- **Strict Mode:** Enabled
- **Testing:** Vitest

#### Project Structure

```
packages/client-ts/
├── package.json
├── tsconfig.json
├── src/
│   ├── index.ts
│   ├── client.ts
│   └── types.ts
└── tests/
```

## Dependency Locking

### .NET

- `Directory.Packages.props` for central package versions
- `global.json` for SDK version
- `packages.lock.json` committed
- Restore with `--locked-mode`

### Python

- `uv.lock` committed
- `uv sync --locked` for CI
- No version ranges in `pyproject.toml`

### npm

- `package-lock.json` committed
- `npm ci` for CI
- Exact versions for prod dependencies

## Stable Contracts

### API Versioning

- Schema version in JSON: `"schemaVersion": "openconnect.v1"`
- Breaking changes require major version bump
- Compatibility tests in CI

### Enum Wire Format

```json
{
  "executionMode": "interactive",
  "privacyMode": "strict-local",
  "detector": "rules"
}
```

- Kebab-case for wire format
- No numeric values
- Unknown values rejected

### Contract Tests

- JSON Schema validation
- Cross-language parity tests
- Backward compatibility check

## Build and Test

### .NET Build

```bash
dotnet restore OpenConnect.slnx --locked-mode
dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror
dotnet test OpenConnect.slnx -c Release --no-build
```

### Python Build

```bash
cd workers/{name}
uv sync --locked
uv run ruff check .
uv run mypy src
uv run pytest -q
```

### npm Build

```bash
cd packages/client-ts
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

## Test-First Development

### Security Components

Tests required before implementation:

- Cross-tenant isolation
- Authorization bypass attempts
- Token replay
- Egress canaries
- Worker compromise

### Property-Based Testing

For critical paths:

- Token generation (collision resistance)
- Streaming split handling
- NATS message deduplication
- Concurrent task acceptance

### Adversarial Testing

- Malicious input fixtures
- Injection attempts
- Race conditions
- Resource exhaustion

## Testing Categories

### Unit Tests

- Fast, isolated
- No database, no network
- Mock external dependencies
- 100% pass rate required

### Integration Tests

- Testcontainers for PostgreSQL, NATS
- Real components
- Redacted output
- May use fake workers

### Contract Tests

- JSON Schema fixtures
- Cross-language parity
- API compatibility

### Security Tests

- Adversarial fixtures
- Canary probes
- Authorization matrices

### Package Consumer Tests

- Install published packages
- Compile against SDK
- Run sample code

## CI Requirements

### Required Checks

- [ ] Build (Release, warnings as errors)
- [ ] Unit tests (all pass)
- [ ] Integration tests (disposable services)
- [ ] Contract tests (parity)
- [ ] Security tests (no leaks)
- [ ] Format/lint checks
- [ ] Package consumer smoke

### PR Policy

- All required checks must pass
- No force pushes to main
- Code owner reviews for sensitive paths
- No merging with failing tests

### Workflow Pinning

- Actions pinned to SHA
- Dependabot for updates
- Manual review for workflow changes

## Documentation

### XML Docs (C#)

```csharp
/// <summary>
/// Registers OpenConnect services with default configuration.
/// </summary>
/// <param name="services">The service collection.</param>
/// <param name="configure">Optional configuration callback.</param>
/// <returns>The service collection for chaining.</returns>
/// <remarks>
/// <para>
/// This method enables privacy protection by default. To disable:
/// </para>
/// <code>
/// services.AddOpenConnect(options => options.Privacy.Enabled = false);
/// </code>
/// </remarks>
public static IServiceCollection AddOpenConnect(
    this IServiceCollection services,
    Action<OpenConnectOptions>? configure = null)
```

### README Requirements

- Project overview
- Installation instructions
- Quick start example
- Configuration reference
- License

### API Reference

- Generated from code
- OpenAPI specification
- JSON Schema reference

## Release Practices

### Semantic Versioning

- MAJOR: Breaking changes
- MINOR: New features, backward compatible
- PATCH: Bug fixes

### Aligned Versions

- All NuGet packages share version
- npm package shares version
- Docker images tagged with version

### Release Checklist

- [ ] All tests pass
- [ ] Documentation updated
- [ ] CHANGELOG.md updated
- [ ] Version bumped
- [ ] Tag created (annotated)
- [ ] Protected environment approval
- [ ] Packages published (OIDC)
- [ ] Images published (signed)
- [ ] Release notes posted

### OIDC Publishing

- No long-lived API keys
- GitHub Actions trusted publisher
- Short-lived tokens
- `id-token: write` only on publish job

## Supply Chain Security

### Dependency Scanning

- Dependabot for .NET, npm
- pip-audit for Python
- Container scanning (Trivy)
- SBOM generation

### Action Pinning

```yaml
- uses: actions/checkout@b4ffde65f46336ab88eb53be808277121392351e # v4.1.1
```

### Image Signing

- cosign keyless signing
- SBOM attestation
- Digest pinning

## Performance

### Benchmarks

- Not required for all features
- Required for hot paths
- Latency percentiles (p50, p95, p99)
- Memory allocation

### Load Testing

- 1000+ events for soak tests
- Concurrent sessions
- Worker failure scenarios

## Monitoring

### OpenTelemetry

- Distributed tracing
- Redacted metrics
- Structured logs

### Health Checks

- `/health/live` - Process liveness
- `/health/ready` - Dependencies ready

### Metrics

- Request latency
- Task throughput
- Privacy blocks
- Authorization denials
- Worker status

## Incident Response

### Build Failure

1. Fix immediately
2. No new commits until green
3. Post-mortem for flaky tests

### Security Issue

1. Private fix
2. Responsible disclosure
3. Patch release
4. Advisory

## Prohibited Practices

- Never commit secrets
- Never disable tests to pass CI
- Never skip security checks
- Never use .Result/.Wait() for async
- Never log sensitive data
- Never trust model output for auth
- Never copy unlicensed code
