using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.Minimal;

/// <summary>
/// Minimal sample DbContext. Registers the OpenConnect model with SQLite-compatible
/// table mapping and owns this sample's migration history table.
/// </summary>
public sealed class MinimalDbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the Minimal sample pipeline.
    /// </summary>
    public const string HistoryTable = "__MinimalMigrationsHistory";

    /// <summary>
    /// Initializes a new instance of the <see cref="MinimalDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public MinimalDbContext(DbContextOptions<MinimalDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel(new OpenConnectModelOptions { UseSqlitePrefix = true });
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
                .MigrationsAssembly(typeof(MinimalDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable));
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> runs against this sample's own context.
/// </summary>
public sealed class MinimalDbContextFactory : IDesignTimeDbContextFactory<MinimalDbContext>
{
    /// <inheritdoc />
    public MinimalDbContext CreateDbContext(string[] args)
    {
        var file = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_MINIMAL_SQLITE_PATH", "minimal-sample.db");
        var options = new DbContextOptionsBuilder<MinimalDbContext>();
        MinimalDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(file));
        return new MinimalDbContext(options.Options);
    }
}
