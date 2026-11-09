using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Samples.SqlServer;

/// <summary>
/// SQL Server sample DbContext. Registers the hardened OpenConnect model with default
/// (non-SQLite) mapping — schema <c>openconnect</c>, twelve tables — via
/// <c>ApplyOpenConnectModel</c> and owns this sample's history table. Exactly one
/// provider (SqlServer) is registered here, per OC-PERSIST-001.
/// </summary>
public sealed class SqlServerSampleDbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the SqlServer sample pipeline.
    /// Lives in <see cref="HistorySchema"/> inside this sample's own database only.
    /// </summary>
    public const string HistoryTable = "__SqlServerMigrationsHistory";

    /// <summary>
    /// Schema holding both the sample tables and its own history table.
    /// Matches <see cref="OpenConnectModelOptions"/> defaults.
    /// </summary>
    public const string HistorySchema = "openconnect";

    /// <summary>
    /// Environment variable holding this sample's SQL Server connection string.
    /// There is no default: a missing value fails closed (never a shared database).
    /// </summary>
    public const string ConnectionStringVariable = "OPENCONNECT_SQLSERVER_CONNECTION";

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerSampleDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public SqlServerSampleDbContext(DbContextOptions<SqlServerSampleDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
    }

    /// <summary>
    /// Applies this sample's SQL Server options: sample connection plus dedicated history table.
    /// </summary>
    /// <param name="options">Options builder.</param>
    /// <param name="connectionString">Resolved SQL Server connection string (never logged).</param>
    public static void ConfigureSqlServer(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseSqlServer(
            connectionString,
            sqlServer => sqlServer
                .MigrationsAssembly(typeof(SqlServerSampleDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable, HistorySchema));
    }

    /// <summary>
    /// Resolves this sample's connection string from the environment.
    /// Throws fail-closed when unset: the sample never falls back to a shared database.
    /// </summary>
    /// <returns>The configured SQL Server connection string. Treat as sensitive: do not log.</returns>
    public static string ResolveConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"sample_sqlserver_no_connection: {ConnectionStringVariable} is not set. " +
                "Set it to this sample's own database connection string (never a shared database).");
        }

        return configured;
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations list</c> runs against this sample's own context.
/// Falls back to an offline localhost placeholder (never connected by design-time inspection)
/// when the environment variable is unset.
/// </summary>
public sealed class SqlServerSampleDbContextFactory : IDesignTimeDbContextFactory<SqlServerSampleDbContext>
{
    /// <inheritdoc />
    public SqlServerSampleDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            SqlServerSampleDbContext.ConnectionStringVariable)
            ?? "Server=localhost;Database=openconnect_ms_sample;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<SqlServerSampleDbContext>();
        SqlServerSampleDbContext.ConfigureSqlServer(options, connectionString);
        return new SqlServerSampleDbContext(options.Options);
    }
}
