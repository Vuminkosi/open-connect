# OpenConnect Persistence Sample — PostgreSQL (`samples/persistence/postgres`)

Sample-owned `ApplicationDbContext` and reviewed migrations for PostgreSQL using `OpenConnect.Persistence.EFCore` (`modelBuilder.ApplyOpenConnectModel()`).

- **Migration Mode**: `OpenConnectMigrationMode.Manual` (worker containers never execute DDL).
- **Environment Variable**: `OPENCONNECT_POSTGRESQL_CONNECTION` (disposable test database only).
- **Commands**:
  - `dotnet run --project samples/persistence/postgres -- migrate`
  - `dotnet run --project samples/persistence/postgres -- verify`
  - `dotnet run --project samples/persistence/postgres -- check-manual-guard`
