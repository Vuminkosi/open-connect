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

## Voice transcript admission customization

The realtime voice relay is domain-neutral by default: it retains its existing
best-final transcript at stream end without application-specific rewriting or early
admission. Provider whitespace trimming, longest-segment selection and accumulated
final-segment merging are unchanged; “unchanged text” refers to that accumulated
transcript, not raw provider frames.

Hosts can register a scoped or singleton `IVoiceTranscriptAdmissionPolicy` using
`services.AddScoped<IVoiceTranscriptAdmissionPolicy, MyVoicePolicy>()` (or
`AddSingleton` for a stateless policy). `ShouldAdmitEarly` sees authoritative
accumulated text. Returning `false` only delays admission: the stream-end fallback
still admits the best final transcript. `NormalizeTranscript` returns non-null task
text for either path. The same policy instance is used throughout one relay.

This is **not** an authorization, rejection, or filtering hook. Identity, session,
existing capture/epoch deduplication and task authorization remain host-enforced;
normalization never grants permission. Custom policies are trusted host code and
must not log transcript contents or treat their decision as task authorization.

The Todo sample explicitly registers its legacy early-admission and word-correction
policy in shared `AddMetaTodoIntegration`, used by both Blazor and worker-host sample
entry points. A pre-registered application policy takes precedence. SDK applications
that relied on the old sample-specific behavior must explicitly opt into their own
policy; none is installed by the core host registration.
