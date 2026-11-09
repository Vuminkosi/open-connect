using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.McpMicroservices.ServiceA;

/// <summary>
/// Microservice A DbContext. Registers the full OpenConnect model (including the
/// G1–G4 RETAIN tables) against this service's own SQLite file with an explicit
/// <c>oc_</c> table prefix, and owns this service's migration history table.
/// </summary>
public sealed class ServiceADbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the microservice A pipeline.
    /// Lives in service A's database file only; never shared with service B.
    /// </summary>
    public const string HistoryTable = "__ServiceAMcpMigrationsHistory";

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceADbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public ServiceADbContext(DbContextOptions<ServiceADbContext> options)
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
    /// Applies this service's SQLite options: file connection plus dedicated history table.
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
                .MigrationsAssembly(typeof(ServiceADbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable));
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> runs against service A's own context.
/// </summary>
public sealed class ServiceADbContextFactory : IDesignTimeDbContextFactory<ServiceADbContext>
{
    /// <inheritdoc />
    public ServiceADbContext CreateDbContext(string[] args)
    {
        var file = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_MICROSERVICE_A_SQLITE_PATH", "microservice-a.db");
        var options = new DbContextOptionsBuilder<ServiceADbContext>();
        ServiceADbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(file));
        return new ServiceADbContext(options.Options);
    }
}
