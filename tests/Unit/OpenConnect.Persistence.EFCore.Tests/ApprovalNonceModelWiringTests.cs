using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Verifies ApplyOpenConnectModel wires ApprovalNonceEntity (WF-04 durable).
/// Model-only inspection; no database connection is opened.
/// Insert-only binding: no F-04-style Version concurrency token (rows are never updated).
/// </summary>
public sealed class ApprovalNonceModelWiringTests
{
    [Fact]
    public void ApplyOpenConnectModel_maps_approval_nonce_to_expected_table()
    {
        using var db = new WiringContext(
            new DbContextOptionsBuilder<WiringContext>()
                .UseSqlite("Data Source=:memory:")
                .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options);

        var entity = db.Model.FindEntityType(typeof(ApprovalNonceEntity));
        Assert.NotNull(entity);
        Assert.Equal("ApprovalNonces", entity.GetTableName());

        var key = Assert.IsAssignableFrom<IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty>>(
            entity.FindPrimaryKey()!.Properties);
        Assert.Equal(["TenantId", "Nonce"], key.Select(p => p.Name));

        Assert.Contains(
            entity.GetIndexes(),
            index => !index.IsUnique &&
                index.Properties.Select(p => p.Name).SequenceEqual(["ApprovalId"]));
    }

    private sealed class WiringContext(DbContextOptions<WiringContext> options)
        : DbContext(options), IOpenConnectModelOptionsProvider
    {
        public OpenConnectModelOptions ModelOptions { get; } = new()
        {
            Schema = "openconnect",
            SqliteTablePrefix = "oc_",
            UseSqlitePrefix = false,
        };

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyOpenConnectModel(ModelOptions);
        }
    }
}
