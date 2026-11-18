# OpenConnect.Navigator.AspNetCore

ASP.NET Core surface for Navigator: HTTP endpoints with session stores and outbox support
(`OpenConnect.Navigator.AspNetCore`, `OpenConnect.Navigator.AspNetCore.Outbox`).

## Install

```bash
dotnet add package OpenConnect.Navigator.AspNetCore
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Navigator.AspNetCore;

services.AddOpenConnectNavigator();
app.MapOpenConnectNavigator();
```

`AddOpenConnectNavigator` (`NavigatorServiceCollectionExtensions.cs:10`) registers the
session stores; `MapOpenConnectNavigator` (`NavigatorEndpoints.cs:13`) maps the HTTP
surface. Add `AddNavigatorOutboxRelay`
(`Outbox/NavigatorOutboxServiceCollectionExtensions.cs:9`) for reliable delivery.

## License

Same terms as the OpenConnect repository.
