# OpenConnect.Hosting.AspNetCore

ASP.NET Core hosting for OpenConnect: endpoint route builders that expose sessions and
execution over HTTP (`OpenConnect.Hosting.AspNetCore`,
`OpenConnect.Hosting.AspNetCore.Execution`).

## Install

```bash
dotnet add package OpenConnect.Hosting.AspNetCore
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;

services.AddOpenConnectTaskExecution();
app.MapOpenConnect();
```

`AddOpenConnectTaskExecution` (`Execution/HostTaskExecutionServiceCollectionExtensions.cs:16`)
registers the task executor; `MapOpenConnect` (`EndpointRouteBuilderExtensions.cs:24`)
exposes the session/execution endpoints. Register `OpenConnect.Core` services first.

## License

Same terms as the OpenConnect repository.
