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
