using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Samples.PostgreSql;

/// <summary>
/// Sample-owned migration DbContext for the outbox E2E sample.
/// Applies the canonical OpenConnect relational schema (<c>openconnect</c> for PostgreSQL/SQL Server,
/// <c>oc_</c> table prefix for SQLite) so the sample or gateway migration pipeline owns DDL
/// while the standalone outbox worker runs strictly with <c>OpenConnectMigrationMode.Manual</c>.
/// </summary>
public sealed class PostgreSqlSampleDbContext(DbContextOptions<PostgreSqlSampleDbContext> options) : DbContext(options), IOpenConnectModelOptionsProvider
{
    public const string HistoryTable = "__PostgreSqlMigrationsHistory";
    public const string HistorySchema = "openconnect";

    public OpenConnectModelOptions ModelOptions { get; init; } = new()
    {
        Schema = HistorySchema,
        SqliteTablePrefix = "oc_",
        UseSqlitePrefix = true,
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel(ModelOptions);
    }
}
