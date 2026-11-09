using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Verifies ApplyOpenConnectModel wires PublicSessionMappingEntity (F-01/F-02 follow-up).
/// Model-only inspection; no database connection is opened.
/// </summary>
public sealed class PublicSessionMappingModelWiringTests
{
    [Fact]
    public void ApplyOpenConnectModel_maps_public_session_mapping_to_expected_table()
    {
        using var db = new WiringContext(
            new DbContextOptionsBuilder<WiringContext>()
                .UseSqlite("Data Source=:memory:")
                .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options);

        var entity = db.Model.FindEntityType(typeof(PublicSessionMappingEntity));
        Assert.NotNull(entity);
        Assert.Equal("PublicSessionMappings", entity.GetTableName());

        var key = Assert.IsAssignableFrom<IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty>>(
            entity.FindPrimaryKey()!.Properties);
        Assert.Equal(["TenantId", "PublicId"], key.Select(p => p.Name));

        Assert.Contains(
            entity.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(p => p.Name).SequenceEqual(["InternalSessionId"]));
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
