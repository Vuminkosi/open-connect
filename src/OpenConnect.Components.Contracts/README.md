# OpenConnect.Components.Contracts

Contracts and options shared between OpenConnect Blazor components and their hosts:
composer sessions, the component registry, and related abstractions
(`OpenConnect.Components.Contracts`).

## Install

```bash
dotnet add package OpenConnect.Components.Contracts
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Components.Contracts;

bool ok = OpenConnectComponentDescriptor.IsValidName("weather-card");
```

This package ships contracts only (no service registration): `ComposerMessage`
(`ComposerContracts.cs:7`), `OpenConnectComponentDescriptor`
(`ComponentContracts.cs:8`), and `ComposerWireState` (`ComposerContracts.cs:108`).
Register hosting services with `AddOpenConnectComponents` in
`OpenConnect.Components.Blazor`.

## License

Same terms as the OpenConnect repository.
