# OpenConnect.Agents

MAF-first agent execution for OpenConnect: real Microsoft Agent Framework runtime agents
with an agent catalog, delegation gates, and run budgets layered above the MAF runtime
(`OpenConnect.Agents`, `OpenConnect.Agents.Catalog`). Unsupported orchestration profiles
fail closed before any model call is attempted.

## Install

```bash
dotnet add package OpenConnect.Agents
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Explore the `OpenConnect.Agents` and `OpenConnect.Agents.Catalog` namespaces for the
execution building blocks, and compose them through `OpenConnect.Core` service
registration. See the repository docs (`docs/`) for supported execution profiles.

## License

Same terms as the OpenConnect repository.
