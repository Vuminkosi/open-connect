using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.Blazor;

/// <summary>
/// Blazor sample DbContext. Registers the OpenConnect model with SQLite-compatible
/// table mapping and owns this sample's migration history table.
/// </summary>
public sealed class BlazorDbContext : DbContext
{
    /// <summary>
    /// Dedicated EF migrations history table owned by the Blazor sample pipeline.
    /// </summary>
    public const string HistoryTable = "__BlazorMigrationsHistory";

    /// <summary>
    /// Initializes a new instance of the <see cref="BlazorDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public BlazorDbContext(DbContextOptions<BlazorDbContext> options)
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
                .MigrationsAssembly(typeof(BlazorDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> runs against this sample's own context.
/// </summary>
public sealed class BlazorDbContextFactory : IDesignTimeDbContextFactory<BlazorDbContext>
{
    /// <inheritdoc />
    public BlazorDbContext CreateDbContext(string[] args)
    {
        var file = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_BLAZOR_SQLITE_PATH", "blazor-sample.db");
        var options = new DbContextOptionsBuilder<BlazorDbContext>();
        BlazorDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(file));
        return new BlazorDbContext(options.Options);
    }
}
