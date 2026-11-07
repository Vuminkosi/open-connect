using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.Context;

public sealed class OpenConnectDbContext(DbContextOptions<OpenConnectDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
    }
}
