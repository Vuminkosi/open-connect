using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.Persistence.Sqlite;

/// <summary>
/// SQLite sample <c>ApplicationDbContext</c> per <c>workers-samples-e2e-spec.md</c> §1 &amp; §3.
/// Registers the OpenConnect relational model via <c>ApplyOpenConnectModel()</c> with explicit
/// <c>oc_</c> table prefix and owns reviewed migrations under <c>samples/persistence/sqlite/Migrations</c>.
/// Worker containers never run DDL; migrations are applied explicitly by this sample pipeline.
/// </summary>
public sealed class ApplicationDbContext : DbContext
{
    public const string HistoryTable = "__SqliteMigrationsHistory";
    public const string PathVariable = "OPENCONNECT_SQLITE_PATH";

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel(new OpenConnectModelOptions
        {
            UseSqlitePrefix = true,
            SqliteTablePrefix = "oc_"
        });
    }

    public static void ConfigureSqlite(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseSqlite(
            connectionString,
            sqlite => sqlite
                .MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable));
    }
}

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var file = SampleEnvironment.ResolveSqliteFile(ApplicationDbContext.PathVariable, "sqlite-persistence-sample.db");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        ApplicationDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(file));
        return new ApplicationDbContext(options.Options);
    }
}
