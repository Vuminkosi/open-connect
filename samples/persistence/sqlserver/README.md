# OpenConnect Persistence Sample — SQL Server (`samples/persistence/sqlserver`)

Sample-owned `ApplicationDbContext` and reviewed migrations for SQL Server using `OpenConnect.Persistence.EFCore` (`modelBuilder.ApplyOpenConnectModel()`).

- **Migration Mode**: `OpenConnectMigrationMode.Manual` (worker containers never execute DDL).
- **Environment Variable**: `OPENCONNECT_SQLSERVER_CONNECTION` (disposable test database only).
- **Commands**:
  - `dotnet run --project samples/persistence/sqlserver -- migrate`
  - `dotnet run --project samples/persistence/sqlserver -- verify`
  - `dotnet run --project samples/persistence/sqlserver -- check-manual-guard`
