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

## Optional worker capability monitoring

Hosts can opt in to polling private worker capability endpoints. The monitor
registers only explicitly scoped logical IDs that the corresponding worker
reports as ready, and marks those IDs stale when the response is unavailable,
invalid or no longer advertises them. It supports the structured expansion-worker
response and the existing inference worker response. The configured IDs must be
exclusively owned by this monitor in the host registry; do not register them
from another component. The registry lease must exceed the polling interval and
the time needed to poll all configured sources.

```csharp
using OpenConnect.Hosting.AspNetCore;

services.AddOpenConnectWorkerCapabilityMonitor([
    new WorkerCapabilitySource("<protected-unix-socket>", "<worker-token>", ["documents.analyze"]),
    new WorkerCapabilitySource("https://<private-knowledge-worker>", "<worker-token>", ["knowledge.embed", "knowledge.rerank"]),
    new WorkerCapabilitySource("https://<private-inference-worker>", "<worker-token>", ["documents.extract"], inference: true)
]);
```

Use protected Unix sockets or HTTPS with verified server identity. Plain HTTP
requires explicit opt-in and an isolated, protected network. Supply credentials
from the host secret store. The monitor is disabled unless registered, and it
uses a 15-second polling interval by default. A ready advertisement is a
readiness signal, not an entitlement, privacy authorization, or proof of a
successful execution. The authenticated `/capabilities/ready` endpoint returns
ready, non-expired host registry descriptors; applications still authorize and
privacy-screen each worker request.

## License

Same terms as the OpenConnect repository.
