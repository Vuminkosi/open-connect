using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.Tests;

public abstract class HostMigrationContext : DbContext, IOpenConnectModelOptionsProvider
{
    protected HostMigrationContext(DbContextOptions options) : base(options) { }
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public OpenConnectModelOptions ModelOptions { get; } = new()
    {
        Schema = "openconnect", SqliteTablePrefix = "oc_", UseSqlitePrefix = false
    };
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().ToTable("Customers").HasKey(customer => customer.Id);
        modelBuilder.Entity<Order>().ToTable("Orders").HasKey(order => order.Id);
        modelBuilder.ApplyOpenConnectModel(ModelOptions);
    }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>();
}

public sealed class HostPgContext(DbContextOptions<HostPgContext> options) : HostMigrationContext(options);
public sealed class HostMsContext(DbContextOptions<HostMsContext> options) : HostMigrationContext(options);

public sealed class Customer { public Guid Id { get; set; } public string Name { get; set; } = ""; }
public sealed class Order { public Guid Id { get; set; } }
