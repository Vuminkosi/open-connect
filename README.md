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
│   ├── OpenConnect.Sessions/      # Session management
│   ├── OpenConnect.Tools/         # Tool broker
│   ├── OpenConnect.Transport.Nats/
│   ├── OpenConnect.Persistence.Postgres/
│   └── OpenConnect.Hosting.AspNetCore/
├── packages/
│   └── client-ts/                 # TypeScript SDK
│   └── client-blazor/                 # .Net Blazor SDK
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

- .NET 8 SDK
- PostgreSQL 16+
- NATS Server 2.10+ (with JetStream)
- Python 3.12+ (for workers)
- Node.js 24+ (for TypeScript client)

## Development

```bash
# Restore dependencies
dotnet restore --locked-mode

# Build
dotnet build

# Run tests
dotnet test

# Start infrastructure (Docker)
docker-compose -f infra/compose.yml up -d
```

## Documentation

- [Architecture](docs/architecture/README.md)
- [Getting Started](docs/getting-started/README.md)
- [API Reference](docs/api/README.md)
- [Configuration](docs/configuration/README.md)

## Contributing

Contributions are welcome! Please read our [Contributing Guide](CONTRIBUTING.md) for details.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
