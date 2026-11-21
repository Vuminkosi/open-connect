---
title: Todo sample tutorial
version: v1
summary: Run the existing Blazor Todo reference application without changing its contract.
order: 40
---

# Todo sample tutorial

The Todo reference application lives in `samples/Blazor`. It combines a .NET 10 Blazor application with the OpenConnect client, composer and optional LiveUI integration. It supports an external gateway and a development-only same-host gateway.

## Local run

Build the sample from the repository root:

```powershell
dotnet run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj
```

External gateway mode is the default. Set `GATEWAY_URL` to the authenticated gateway origin and configure credentials appropriate to that host. The container-oriented default hostname is intended for its Compose network; it is not automatically reachable from a process running directly on your machine. Use protected transport outside an isolated development environment.

For a same-host development trial, the sample README documents `OpenConnect__Gateway__Mode=SameHostGateway`, the sample base URL, model settings and the internal Todo credential. Same-host development authentication is a sample mechanism and is not a production identity scheme. The document and knowledge examples have additional opt-in workers and configuration. Supply secrets outside source control.

## Important boundaries

The sample stores Todo items and OpenConnect state in its application-owned SQLite database and applies its sample migrations on startup. Persist the database file if items must survive a container replacement; use a disposable database for a trial and review migrations before using existing data. Navigator, voice and model-backed worker behavior is optional and must not be inferred from the visible composer shell. See `samples/Blazor/README.md` for current settings and separate browser, model and worker verification lanes.

## Related links

- [Composer and UI integration](/docs/v1/ui-integration)
- [Examples and sample projects](/docs/v1/examples)
