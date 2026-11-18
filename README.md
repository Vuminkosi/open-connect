# OpenConnect

**Build AI solutions, not AI infrastructure.**

OpenConnect is an open-source, .NET-first, privacy-first, model-agnostic AI application framework for developers. It provides reusable libraries, secure defaults, and modular components that let developers focus on building AI-powered applications instead of wrestling with infrastructure.

## Features

- **Developer-First**: Simple `services.AddOpenConnect()` gets you running in minutes
- **Privacy-First**: Privacy firewall enabled by default with local detection for sensitive data
- **Model-Agnostic**: Works with any LLM provider (OpenAI, Azure, Anthropic, local models)
- **Framework, Not Platform**: Layered on Microsoft Agent Framework, not competing with it
- **Durable by Default**: PostgreSQL + NATS for reliable multi-turn conversations

## Quick Start

```csharp
// Program.cs
builder.Services.AddOpenConnect();
builder.Services.AddOpenConnectPostgres(connectionString);
builder.Services.AddOpenConnectNats(natsUrl);

var app = builder.Build();
app.MapOpenConnect();
app.Run();
```

## Architecture

OpenConnect follows a layered architecture:

- **Gateway Layer**: ASP.NET Core hosting with HTTP/WebSocket endpoints
- **Agent Layer**: Microsoft Agent Framework (MAF) integration
- **Privacy Layer**: Firewall, policy engine, tokenization, and egress gates
- **Runtime Layer**: Agent registry, tool broker, capability router
- **Transport Layer**: NATS JetStream for event backbone and worker protocol
- **Persistence Layer**: PostgreSQL for sessions, tasks, events, and tokens
- **Worker Layer**: Optional Python services for specialized capabilities

## Project Structure

```
openconnect/
├── src/                           # .NET libraries
│   ├── OpenConnect.Abstractions/  # Core interfaces and DTOs
│   ├── OpenConnect.Core/          # Implementation and DI
│   ├── OpenConnect.Agents/        # MAF integration
│   ├── OpenConnect.Privacy/       # Privacy firewall
│   ├── OpenConnect.Tools/         # Tool broker
│   ├── OpenConnect.Transport.Nats/
│   ├── OpenConnect.Persistence.EFCore/
│   └── OpenConnect.Hosting.AspNetCore/
├── packages/
│   └── client-ts/                 # TypeScript SDK
├── workers/
│   ├── security/                  # Privacy detection
│   ├── classification/
│   ├── mms-tts/
│   ├── nllb/
│   └── ...
├── contracts/
│   ├── openapi/
│   └── jsonschema/
└── tests/
```

## Requirements

- .NET SDK from [global.json](global.json) (currently 10.0.404); libraries target .NET 8, 9, and 10, while executables and tests default to .NET 10
- PostgreSQL 16+
- NATS Server 2.10+ (with JetStream)
- Python 3.12+ (for workers)
- Node.js 24+ (for TypeScript client)

## Development

```bash
# Restore dependencies
dotnet restore OpenConnect.slnx --locked-mode

# Build
dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror

# Run tests
dotnet test OpenConnect.slnx -c Release --no-build --filter "Category!=NatsLive"

# Start infrastructure (Docker)
docker compose -f infra/compose.yml up -d
```

### CI checks

The [main CI workflow](.github/workflows/ci.yml) also checks formatting (`dotnet format OpenConnect.slnx --verify-no-changes --no-restore`) and runs security and contract checks through `scripts/verify.ps1`. Live NATS tests require an explicit broker and are excluded from the default test selection above.

Reproduce one [worker contract leg](.github/workflows/worker-sample-e2e.yml) on Linux:

```bash
./samples/workers/shared/scripts/verify-no-build-models.sh classification
./samples/workers/shared/scripts/run-one.sh classification --tier contract --provider sqlite
```

See [worker samples](samples/workers/README.md) for the full matrix and infrastructure/model prerequisites. The [documentation workflow](.github/workflows/docs-portal.yml) separately validates the [portal](site/OpenConnect.Docs/README.md). Local unit tests alone do not verify these workflows.

## Documentation

- [Framework compatibility](docs/compatibility/framework-matrix.md)
- [Getting Started](site/OpenConnect.Docs/Content/v1/getting-started.md)
- [API Reference](site/OpenConnect.Docs/Content/v1/api-reference.md)
- [Configuration](site/OpenConnect.Docs/Content/v1/configuration.md)

## Contributing

Contributions are welcome! Please read the [development guide](AGENTS.md) for details.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
