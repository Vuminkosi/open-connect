# OpenConnect.Persistence.EFCore

EF Core persistence for OpenConnect: DbContext, entity configurations, transactional
stores, and outbox support (`OpenConnect.Persistence.EFCore.Configuration`,
`OpenConnect.Persistence.EFCore.Context`). Migrations are owned by the sample apps.

## Install

```bash
dotnet add package OpenConnect.Persistence.EFCore
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## Usage

Register the EF Core stores from the `OpenConnect.Persistence.EFCore` namespaces through
service registration, pointing them at your provider connection. See the repository docs
(`docs/`) and the samples for migration ownership and configuration.

## License

Same terms as the OpenConnect repository.
