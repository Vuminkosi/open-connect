using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class HostMigrationContext(DbContextOptions<HostMigrationContext> options) : DbContext(options), IOpenConnectModelOptionsProvider
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    public OpenConnectModelOptions ModelOptions { get; } = new()
    {
        Schema = "openconnect",
        SqliteTablePrefix = "oc_",
        UseSqlitePrefix = false
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

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Order
{
    public int Id { get; set; }
}

public sealed class HostMigrationContextFactory : IDesignTimeDbContextFactory<HostMigrationContext>
{
    public HostMigrationContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HostMigrationContext>()
            .UseNpgsql("Host=localhost;Database=designtime")
            .Options);
}
