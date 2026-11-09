using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.EntityConfigurations;

internal sealed class PublicSessionMappingEntityConfiguration(OpenConnectModelOptions mapping)
    : OpenConnectEntityConfiguration<PublicSessionMappingEntity>(mapping, "PublicSessionMappings")
{
    public override void Configure(EntityTypeBuilder<PublicSessionMappingEntity> builder)
    {
        Map(builder);

        // Tenant-partitioned identity: (TenantId, PublicId) is the primary key, hence unique.
        builder.HasKey(x => new { x.TenantId, x.PublicId });

        builder.Property(x => x.PublicId).IsRequired();
        builder.Property(x => x.InternalSessionId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.TenantId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.PrincipalId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();

        // One runtime session resolves to at most one public UUID.
        builder.HasIndex(x => x.InternalSessionId).IsUnique();
    }
}
