---
title: Examples and sample projects
version: v1
summary: Find an executable sample for a host, persistence provider, UI or worker.
order: 45
---

# Examples and sample projects

The source repository contains runnable samples for different parts of the SDK. Select a sample for your integration, read its README and prerequisites, and run it in an isolated environment. A sample test or simulated model is not evidence that a production provider, model or user journey is ready.

| Goal | Source sample | What it demonstrates |
| --- | --- | --- |
| Basic persistence | [Minimal](https://github.com/Vuminkosi/open-connect/tree/master/samples/Minimal) | Application-owned SQLite context, migration and schema verification |
| Development-only persistence | [InMemory](https://github.com/Vuminkosi/open-connect/tree/master/samples/InMemory) | In-process EF provider; data is lost on exit |
| Relational providers | [SQLite](https://github.com/Vuminkosi/open-connect/tree/master/samples/Sqlite), [PostgreSQL](https://github.com/Vuminkosi/open-connect/tree/master/samples/PostgreSql), [SQL Server](https://github.com/Vuminkosi/open-connect/tree/master/samples/SqlServer) | Provider-specific contexts, migrations and checks |
| Blazor application | [Todo](https://github.com/Vuminkosi/open-connect/tree/master/samples/Blazor) | Composer, gateway modes and optional LiveUI, document and knowledge examples |
| Guided navigation | [Navigator](https://github.com/Vuminkosi/open-connect/tree/master/samples/Navigator) | Navigator host and client composition |
| Worker integration | [Worker samples](https://github.com/Vuminkosi/open-connect/tree/master/samples/workers) | Individual worker deployments and tiered contract, integration and model-live checks |
| MCP services | [MCP microservices](https://github.com/Vuminkosi/open-connect/tree/master/samples/McpMicroservices) | Sample service integration and its required host configuration |
| Browser composer | [Composer package](https://github.com/Vuminkosi/open-connect/tree/master/packages/composer) | Vanilla and React composition and lifecycle |

These links track the repository's `master` branch. For a released package, use the corresponding release source and verify package and sample compatibility.

## First source-only check

The InMemory sample does not require a relational database. From the repository root with the pinned .NET SDK installed:

```powershell
dotnet restore samples/InMemory/OpenConnect.Samples.InMemory.csproj --locked-mode
dotnet run --project samples/InMemory/OpenConnect.Samples.InMemory.csproj --no-restore -- verify
```

This verifies its in-process model and reachability only. It does not test a gateway, durable storage, a model provider or a worker. For a real gateway task, continue with the [.NET client](/docs/v1/dotnet-client) or [TypeScript client](/docs/v1/typescript-client) after configuring an authenticated host.

## Related links

- [Getting started](/docs/v1/getting-started)
- [Supported services and integrations](/docs/v1/supported-services)
