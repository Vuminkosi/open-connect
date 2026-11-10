# OpenConnect.Persistence.EFCore

EF Core persistence for OpenConnect: DbContext, entity configurations, transactional
stores, and outbox support (`OpenConnect.Persistence.EFCore.Configuration`,
`OpenConnect.Persistence.EFCore.Context`). Migrations are owned by the sample apps.

## Install

```bash
dotnet add package OpenConnect.Persistence.EFCore
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Persistence.EFCore.DependencyInjection;

services.AddDbContext<SupportDbContext>(db => db.UseNpgsql(connectionString));
services.AddOpenConnectEntityFramework<SupportDbContext>();
```

`AddOpenConnectEntityFramework<TContext>`
(`DependencyInjection/OpenConnectEfServiceCollectionExtensions.cs:18`) wires the EF Core
task/event/outbox/session/lease stores; the `DbContext` must be registered first or it
throws. EFCore-only: migrations are owned by the sample apps, never by this package.

## License

Same terms as the OpenConnect repository.
