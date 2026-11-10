# OpenConnect.Components.Blazor

Blazor UI kit for OpenConnect interactive sessions: mini-composer, launcher, status line,
and provider wiring (`OpenConnect.Components.Blazor`), composed against the host's
existing services.

## Install

```bash
dotnet add package OpenConnect.Components.Blazor
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Components.Blazor;

services.AddOpenConnectComponents(); // component catalogue + ComposerSession
services.AddOpenConnectComposer();   // anchored composer (FAB + bubble)
```

`AddOpenConnectComponents` (`ServiceCollectionExtensions.cs:14`) and
`AddOpenConnectComposer` (`:26`) only register library services — the host app keeps
ownership of the MudBlazor providers (`MudThemeProvider`, `MudDialogProvider`,
`MudSnackbarProvider`, `MudPopoverProvider`). Pair with
`OpenConnect.Components.Contracts` for descriptors and composer options.

## License

Same terms as the OpenConnect repository.
