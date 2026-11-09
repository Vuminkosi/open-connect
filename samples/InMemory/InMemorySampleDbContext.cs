using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Samples.InMemory;

/// <summary>
/// Dev-only InMemory DbContext. NON-DURABLE: all state is lost on process exit.
/// Never use for production data. There is intentionally no <c>Migrations/</c> directory:
/// the InMemory provider has no relational migration pipeline.
/// </summary>
public sealed class InMemorySampleDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InMemorySampleDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public InMemorySampleDbContext(DbContextOptions<InMemorySampleDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
    }
}
