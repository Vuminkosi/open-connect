using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Samples.Persistence.SqlServer;

/// <summary>
/// SQL Server sample <c>ApplicationDbContext</c> per <c>workers-samples-e2e-spec.md</c> §1 &amp; §3.
/// Registers the OpenConnect relational model via <c>ApplyOpenConnectModel()</c> and owns
/// reviewed migrations under <c>samples/persistence/sqlserver/Migrations</c>.
/// Worker containers never run DDL; migrations are applied explicitly by this sample pipeline.
/// </summary>
public sealed class ApplicationDbContext : DbContext
{
    public const string HistoryTable = "__SqlServerMigrationsHistory";
    public const string HistorySchema = "openconnect";
    public const string ConnectionStringVariable = "OPENCONNECT_SQLSERVER_CONNECTION";

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
    }

    public static void ConfigureSqlServer(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseSqlServer(
            connectionString,
            sqlServer => sqlServer
                .MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable, HistorySchema));
    }

    public static string ResolveConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"sample_sqlserver_no_connection: {ConnectionStringVariable} is not set. " +
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
            ?? "Server=localhost;Database=openconnect_ms_sample;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        ApplicationDbContext.ConfigureSqlServer(options, connectionString);
        return new ApplicationDbContext(options.Options);
    }
}
