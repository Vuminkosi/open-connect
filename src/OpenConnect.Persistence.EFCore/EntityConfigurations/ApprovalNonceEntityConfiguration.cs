using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.EntityConfigurations;

internal sealed class ApprovalNonceEntityConfiguration(OpenConnectModelOptions mapping)
    : OpenConnectEntityConfiguration<ApprovalNonceEntity>(mapping, "ApprovalNonces")
{
    public override void Configure(EntityTypeBuilder<ApprovalNonceEntity> builder)
    {
        Map(builder);

        // Tenant-partitioned identity: (TenantId, Nonce) is the primary key, hence unique.
        builder.HasKey(x => new { x.TenantId, x.Nonce });

        builder.Property(x => x.TenantId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.Nonce).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.ApprovalId).IsRequired();
        builder.Property(x => x.PrincipalId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        // Audit lookup: find the nonce binding for a given approval.
        builder.HasIndex(x => x.ApprovalId);
    }
}
