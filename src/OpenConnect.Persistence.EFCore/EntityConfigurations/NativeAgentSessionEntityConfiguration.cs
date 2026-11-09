using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.EntityConfigurations;

internal sealed class NativeAgentSessionEntityConfiguration(OpenConnectModelOptions mapping)
    : OpenConnectEntityConfiguration<NativeAgentSessionEntity>(mapping, "NativeAgentSessions")
{
    public override void Configure(EntityTypeBuilder<NativeAgentSessionEntity> builder)
    {
        Map(builder);
        builder.HasKey(x => x.BindingKey);
        builder.Property(x => x.BindingKey).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.SessionId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.TenantId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.PrincipalId).HasMaxLength(26).IsUnicode(false).IsRequired();
        builder.Property(x => x.AgentId).IsRequired();
        builder.Property(x => x.DefinitionRevision).IsRequired();
        builder.Property(x => x.MafVersion).IsRequired();
        builder.Property(x => x.SchemaVersion).IsRequired();
        builder.Property(x => x.ProviderId).IsRequired();
        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.Ciphertext).IsRequired();
    }
}
