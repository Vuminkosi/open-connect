---
title: API reference status
version: v1
summary: The generated API reference is built from actual assemblies and contracts.
order: 50
---

# API reference status

The portal's API reference pipeline is evidence-gated. Public API pages must be generated from compiled OpenConnect assemblies/XML comments and the versioned OpenAPI contract, not handwritten method names.

Current source API entry points include `AddOpenConnect`, `AddOpenConnectClient`, `AddOpenConnectComponents`, `AddOpenConnectComposer` and `MapOpenConnect`. Verify exact overloads against the compiled package before publishing examples.

DocFX generation and package-consumer validation remain separate build tasks from the static portal host.

## Find a contract

Use the [.NET client guide](/docs/v1/dotnet-client) for typed task, session and event calls, the [TypeScript guide](/docs/v1/typescript-client) for the fetch client, and the [HTTP API](/docs/v1/rest-api) for gateway routes. The source repository's `contracts/openapi/openconnect.v1.yaml` describes the versioned HTTP contract; JSON Schemas are under `contracts`. Public API signatures are generated from the .NET projects, not from the examples on this site.

## Generate the reference from source

The repository pins DocFX as a local .NET tool. From the repository root, restore tools and build the solution before generating:

```powershell
dotnet tool restore
dotnet restore OpenConnect.slnx --locked-mode
dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror -m:1
./scripts/generate-docs-api.ps1 -Mode all
```

The script writes generated artifacts beneath `site/OpenConnect.Docs/generated/`; these artifacts are not committed. A portal running without them can still serve the authored guides. Use the source and release-specific package metadata to resolve version differences, and run package-consumer checks separately before claiming a published release is usable.

## Related links

- [Gateway HTTP API](/docs/v1/rest-api)
- [Examples and sample projects](/docs/v1/examples)
