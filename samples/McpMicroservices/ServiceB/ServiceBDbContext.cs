using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.McpMicroservices.ServiceB;

/// <summary>
/// Microservice B DbContext. Registers the full OpenConnect model (including the
/// G1–G4 RETAIN tables) against this service's own SQLite file with an explicit
/// <c>oc_</c> table prefix, and owns this service's migration history table.
/// The schema matches service A's; isolation comes from separate database files,
/// separate histories, and tenant-scoped reads — not from different table names.
/// </summary>
public sealed class ServiceBDbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the microservice B pipeline.
    /// Lives in service B's database file only; never shared with service A.
    /// </summary>
    public const string HistoryTable = "__ServiceBMcpMigrationsHistory";

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceBDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public ServiceBDbContext(DbContextOptions<ServiceBDbContext> options)
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
                .MigrationsAssembly(typeof(ServiceBDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable));
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> runs against service B's own context.
/// </summary>
public sealed class ServiceBDbContextFactory : IDesignTimeDbContextFactory<ServiceBDbContext>
{
    /// <inheritdoc />
    public ServiceBDbContext CreateDbContext(string[] args)
    {
        var file = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_MICROSERVICE_B_SQLITE_PATH", "microservice-b.db");
        var options = new DbContextOptionsBuilder<ServiceBDbContext>();
        ServiceBDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(file));
        return new ServiceBDbContext(options.Options);
    }
}
