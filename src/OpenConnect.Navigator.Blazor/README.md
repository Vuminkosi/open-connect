# OpenConnect.Navigator.Blazor

Blazor adapter for Navigator: renders navigator state and commands inside Blazor apps
(`OpenConnect.Navigator.Blazor`).

## Install

```bash
dotnet add package OpenConnect.Navigator.Blazor
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Navigator.Blazor;

services.AddOpenConnectNavigator();

string text = NavigatorTourService.RenderTextTour(steps);
```

`AddOpenConnectNavigator` (`ServiceCollectionExtensions.cs:18`) registers the Blazor
adapter; `NavigatorTourService.RenderTextTour` (`NavigatorTourService.cs:366`) renders a
tour plan (`IReadOnlyList<NavigatorTourStep>`, `NavigatorTourStep.cs:13`) as text.
Back it with the core `OpenConnect.Navigator` services.

## License

Same terms as the OpenConnect repository.
