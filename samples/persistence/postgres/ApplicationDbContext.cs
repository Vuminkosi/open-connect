using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Samples.Persistence.Postgres;

/// <summary>
/// PostgreSQL sample <c>ApplicationDbContext</c> per <c>workers-samples-e2e-spec.md</c> §1 &amp; §3.
/// Registers the OpenConnect relational model via <c>ApplyOpenConnectModel()</c> and owns
/// reviewed migrations under <c>samples/persistence/postgres/Migrations</c>.
/// Worker containers never run DDL; migrations are applied explicitly by this sample pipeline.
/// </summary>
public sealed class ApplicationDbContext : DbContext
{
    public const string HistoryTable = "__PostgreSqlMigrationsHistory";
    public const string HistorySchema = "openconnect";
    public const string ConnectionStringVariable = "OPENCONNECT_POSTGRESQL_CONNECTION";

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
    }

    public static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseNpgsql(
            connectionString,
            npgsql => npgsql
                .MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable, HistorySchema));
    }

    public static string ResolveConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"sample_postgres_no_connection: {ConnectionStringVariable} is not set. " +
                "Set it to a disposable test database connection string (never a shared or production database).");
        }

        return configured;
    }
}

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ApplicationDbContext.ConnectionStringVariable)
            ?? "Host=localhost;Port=5432;Database=openconnect_pg_sample;Username=postgres";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        ApplicationDbContext.ConfigureNpgsql(options, connectionString);
        return new ApplicationDbContext(options.Options);
    }
}
