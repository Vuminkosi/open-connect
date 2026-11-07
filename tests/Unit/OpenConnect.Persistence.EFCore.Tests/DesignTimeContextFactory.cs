using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenConnect.Persistence.EFCore.Context;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<OpenConnectDbContext>
{
    public OpenConnectDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseNpgsql("Host=localhost;Database=designtime")
            .Options);
}
