# OpenConnect Persistence Sample — SQLite (`samples/persistence/sqlite`)

Sample-owned `ApplicationDbContext` and reviewed migrations for SQLite using `OpenConnect.Persistence.EFCore` (`modelBuilder.ApplyOpenConnectModel()`).

- **Migration Mode**: `OpenConnectMigrationMode.Manual` (worker containers never execute DDL).
- **Environment Variable**: `OPENCONNECT_SQLITE_PATH` (disposable local test database file only).
- **Commands**:
  - `dotnet run --project samples/persistence/sqlite -- migrate`
  - `dotnet run --project samples/persistence/sqlite -- verify`
  - `dotnet run --project samples/persistence/sqlite -- check-manual-guard`
