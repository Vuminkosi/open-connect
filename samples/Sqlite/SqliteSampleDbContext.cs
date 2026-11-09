using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.Sqlite;

/// <summary>
/// SQLite-file sample DbContext. Demonstrates explicit model options (explicit
/// <c>SqliteTablePrefix</c>) while registering <c>ApplyOpenConnectModel</c> and
/// owning this sample's history table.
/// </summary>
public sealed class SqliteSampleDbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the Sqlite sample pipeline.
    /// </summary>
    public const string HistoryTable = "__SqliteMigrationsHistory";

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteSampleDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public SqliteSampleDbContext(DbContextOptions<SqliteSampleDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel(new OpenConnectModelOptions
        {
            UseSqlitePrefix = true,
            SqliteTablePrefix = "oc_"
        });
    }

    /// <summary>
    /// Applies this sample's SQLite options: file connection plus dedicated history table.
    /// </summary>
    /// <param name="options">Options builder.</param>
    /// <param name="connectionString">Resolved SQLite connection string (never logged).</param>
    public static void ConfigureSqlite(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseSqlite(
            connectionString,
            sqlite => sqlite
                .MigrationsAssembly(typeof(SqliteSampleDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable));
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> runs against this sample's own context.
/// </summary>
public sealed class SqliteSampleDbContextFactory : IDesignTimeDbContextFactory<SqliteSampleDbContext>
{
    /// <inheritdoc />
    public SqliteSampleDbContext CreateDbContext(string[] args)
    {
        var file = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_SQLITE_PATH", "sqlite-sample.db");
        var options = new DbContextOptionsBuilder<SqliteSampleDbContext>();
        SqliteSampleDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(file));
        return new SqliteSampleDbContext(options.Options);
    }
}
