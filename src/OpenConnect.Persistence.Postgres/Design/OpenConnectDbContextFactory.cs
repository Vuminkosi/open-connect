using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenConnect.Persistence.Postgres.Design;

/// <summary>Creates the PostgreSQL context for Entity Framework design-time tools.</summary>
public sealed class OpenConnectDbContextFactory : IDesignTimeDbContextFactory<OpenConnectDbContext>
{
    /// <inheritdoc />
    public OpenConnectDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseNpgsql("Host=localhost;Database=oc_design_check")
            .Options;

        return new OpenConnectDbContext(options);
    }
}
