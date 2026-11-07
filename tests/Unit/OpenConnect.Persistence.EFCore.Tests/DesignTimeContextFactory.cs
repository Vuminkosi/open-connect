using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class HostPgContextFactory : IDesignTimeDbContextFactory<HostPgContext>
{
    public HostPgContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<HostPgContext>().UseNpgsql("Host=localhost;Database=designtime").Options);
}
public sealed class HostMsContextFactory : IDesignTimeDbContextFactory<HostMsContext>
{
    public HostMsContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<HostMsContext>().UseSqlServer("Server=localhost;Database=designtime;TrustServerCertificate=True").Options);
}
