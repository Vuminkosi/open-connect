# OpenConnect.Core

Composition root for OpenConnect: dependency-injection registration, validated options,
capability routing, and runtime services (`OpenConnect.Core`,
`OpenConnect.Core.Configuration`, `OpenConnect.Core.Capabilities`). Privacy protections
are enabled by default.

## Install

```bash
dotnet add package OpenConnect.Core
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Core;

services.AddOpenConnect(options => options.Privacy.Enabled = true);
```

`AddOpenConnect` (`ServiceCollectionExtensions.cs:11`) is the composition root: it
registers `IAgentDefinitionRegistry`, validates `OpenConnectOptions` on start, and keeps
privacy enforcement on by default (disabling it only logs a warning).

## License

Same terms as the OpenConnect repository.
