# OpenConnect.Navigator

Navigator core: authorized navigation commands, sessions, tour plans, and diagnostics
(`OpenConnect.Navigator`, `OpenConnect.Navigator.Capture`,
`OpenConnect.Navigator.Diagnostics`).

## Install

```bash
dotnet add package OpenConnect.Navigator
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Navigator;

services.AddOpenConnectNavigator();

var session = new NavigatorSession(tenantId: "tenant-1", principalId: "user-1");
session.TransitionTo(NavigatorSessionState.Ready);
```

`AddOpenConnectNavigator` (`NavigatorServiceCollectionExtensions.cs:11`) registers the
core services; `NavigatorSession` (`NavigatorSession.cs:3`) enforces the legal state
machine via `TransitionTo` (`:38`) — illegal transitions throw
`InvalidOperationException`. Expose over HTTP with
`OpenConnect.Navigator.AspNetCore`.

## License

Same terms as the OpenConnect repository.
