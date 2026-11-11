---
title: Todo sample tutorial
version: v1
summary: Run the existing Blazor Todo reference application without changing its contract.
order: 40
---

# Todo sample tutorial

The maintained Todo reference application lives in `samples/Blazor`. It is a separate .NET 10 Blazor consumer that uses OpenConnect client/components and a separately hosted gateway.

## Local run

Build the sample from the repository root:

```powershell
dotnet run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj
```

The sample defaults to `GATEWAY_URL=http://gateway-host:8080/` in its container stack. A local developer must point `GATEWAY_URL` at an authenticated OpenConnect gateway and use HTTPS outside a disposable development environment.

## Important boundaries

Todo records currently use the sample's `InMemoryTodoStore`, so Todo changes are process-local. The SQLite context is used for OpenConnect persistence and migrations, not the Todo domain records. Navigator, voice and model-backed worker behavior is optional and must not be inferred from the visible composer shell.
