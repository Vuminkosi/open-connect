# OpenConnect.Client

Typed .NET client for calling an OpenConnect gateway over HTTP: sessions, runs, and
related client operations from the `OpenConnect.Client` namespace.

## Install

```bash
dotnet add package OpenConnect.Client
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Client;

services.AddOpenConnectClient(new Uri("https://openconnect.example/"));

var client = provider.GetRequiredService<IOpenConnectClient>();
TaskRecord task = await client.CreateTaskAsync(new CreateTaskRequest(
    ClientTaskId: "client-1",
    IdempotencyKey: "idem-1",
    Message: "Hello"));
```

`AddOpenConnectClient` (`OpenConnectClientServiceCollectionExtensions.cs:5`) requires an
absolute HTTPS base address; `CreateTaskAsync` (`IOpenConnectClient.cs:5`) accepts a
`CreateTaskRequest` (`IOpenConnectClient.cs:39`). Server side: `MapOpenConnect` in
`OpenConnect.Hosting.AspNetCore`.

## License

Same terms as the OpenConnect repository.
