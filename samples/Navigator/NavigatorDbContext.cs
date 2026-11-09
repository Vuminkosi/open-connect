using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

namespace OpenConnect.Samples.Navigator;

/// <summary>
/// Navigator sample DbContext. Registers the shared OpenConnect model with SQLite-compatible
/// table mapping. This sample owns NO migrations: the lease/outbox adapters use only the
/// shared EFCore-model tables (<c>oc_leases</c>, <c>oc_outbox</c>) in this sample's own SQLite
/// file, and schema is created with <c>EnsureCreatedAsync</c> (see README for the rationale).
/// No new or shared tables are introduced here.
/// </summary>
public sealed class NavigatorDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NavigatorDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public NavigatorDbContext(DbContextOptions<NavigatorDbContext> options)
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
    /// Applies this sample's SQLite options: file connection string only.
    /// </summary>
    /// <param name="options">Options builder.</param>
    /// <param name="connectionString">Resolved SQLite connection string (never logged).</param>
    public static void ConfigureSqlite(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseSqlite(connectionString);
    }
}
