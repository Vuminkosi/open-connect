using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Samples.PostgreSql;

/// <summary>
/// PostgreSQL sample DbContext. Registers the hardened OpenConnect model with default
/// (non-SQLite) mapping — schema <c>openconnect</c>, twelve tables — via
/// <c>ApplyOpenConnectModel</c> and owns this sample's history table. Exactly one
/// provider (Npgsql) is registered here, per OC-PERSIST-001.
/// </summary>
public sealed class PostgreSqlSampleDbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the PostgreSql sample pipeline.
    /// Lives in <see cref="HistorySchema"/> inside this sample's own database only.
    /// </summary>
    public const string HistoryTable = "__PostgreSqlMigrationsHistory";

    /// <summary>
    /// Schema holding both the sample tables and its own history table.
    /// Matches <see cref="OpenConnectModelOptions"/> defaults.
    /// </summary>
    public const string HistorySchema = "openconnect";

    /// <summary>
    /// Environment variable holding this sample's Npgsql connection string.
    /// There is no default: a missing value fails closed (never a shared database).
    /// </summary>
    public const string ConnectionStringVariable = "OPENCONNECT_POSTGRESQL_CONNECTION";

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlSampleDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public PostgreSqlSampleDbContext(DbContextOptions<PostgreSqlSampleDbContext> options)
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
    /// Applies this sample's Npgsql options: sample connection plus dedicated history table.
    /// </summary>
    /// <param name="options">Options builder.</param>
    /// <param name="connectionString">Resolved Npgsql connection string (never logged).</param>
    public static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseNpgsql(
            connectionString,
            npgsql => npgsql
                .MigrationsAssembly(typeof(PostgreSqlSampleDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable, HistorySchema));
    }

    /// <summary>
    /// Resolves this sample's connection string from the environment.
    /// Throws fail-closed when unset: the sample never falls back to a shared database.
    /// </summary>
    /// <returns>The configured Npgsql connection string. Treat as sensitive: do not log.</returns>
    public static string ResolveConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"sample_postgres_no_connection: {ConnectionStringVariable} is not set. " +
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
public sealed class PostgreSqlSampleDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlSampleDbContext>
{
    /// <inheritdoc />
    public PostgreSqlSampleDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlSampleDbContext.ConnectionStringVariable)
            ?? "Host=localhost;Port=5432;Database=openconnect_pg_sample;Username=postgres";
        var options = new DbContextOptionsBuilder<PostgreSqlSampleDbContext>();
        PostgreSqlSampleDbContext.ConfigureNpgsql(options, connectionString);
        return new PostgreSqlSampleDbContext(options.Options);
    }
}
