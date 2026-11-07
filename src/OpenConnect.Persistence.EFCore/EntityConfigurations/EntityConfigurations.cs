using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.EntityConfigurations;

internal abstract class OpenConnectEntityConfiguration<TEntity>(OpenConnectModelOptions mapping, string table)
    : IEntityTypeConfiguration<TEntity> where TEntity : class
{
    protected void Map(EntityTypeBuilder<TEntity> builder) => builder.ToTable(
        mapping.UseSqlitePrefix ? mapping.SqliteTablePrefix + table.ToLowerInvariant() : table,
        mapping.UseSqlitePrefix ? null : mapping.Schema);
    public abstract void Configure(EntityTypeBuilder<TEntity> builder);
}

internal sealed class SessionEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<SessionEntity>(m, "Sessions")
{
    public override void Configure(EntityTypeBuilder<SessionEntity> b) { Map(b); b.HasKey(x => x.SessionId); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.PrincipalScope).HasMaxLength(256).IsRequired(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.TenantScope, x.PrincipalScope }); }
}
internal sealed class TaskEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<TaskEntity>(m, "Tasks")
{
    public override void Configure(EntityTypeBuilder<TaskEntity> b) { Map(b); b.HasKey(x => x.TaskId); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.PrincipalScope).HasMaxLength(256).IsRequired(); b.Property(x => x.WorkloadScope).HasMaxLength(256).IsRequired(); b.Property(x => x.ClientTaskId).HasMaxLength(256).IsRequired(); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.TenantScope, x.PrincipalScope, x.ClientTaskId }).IsUnique(); b.HasIndex(x => x.SessionId); }
}
internal sealed class TurnEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<TurnEntity>(m, "Turns")
{
    public override void Configure(EntityTypeBuilder<TurnEntity> b) { Map(b); b.HasKey(x => x.TurnId); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.PrincipalScope).HasMaxLength(256).IsRequired(); b.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique(); }
}
internal sealed class EventEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<EventEntity>(m, "Events")
{
    public override void Configure(EntityTypeBuilder<EventEntity> b) { Map(b); b.HasKey(x => x.EventId); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.EventType).HasMaxLength(256).IsRequired(); b.HasIndex(x => new { x.TenantScope, x.SessionId, x.Sequence }).IsUnique(); }
}
internal sealed class OutboxEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<OutboxEntity>(m, "Outbox")
{
    public override void Configure(EntityTypeBuilder<OutboxEntity> b) { Map(b); b.HasKey(x => x.Id); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.EventId).HasMaxLength(256).IsRequired(); b.Property(x => x.Subject).HasMaxLength(512).IsRequired(); b.Property(x => x.Status).HasMaxLength(32).IsRequired(); b.Property(x => x.FencingVersion).IsConcurrencyToken(); b.HasIndex(x => new { x.TenantScope, x.EventId }).IsUnique(); b.HasIndex(x => new { x.Status, x.NextAttemptUtc }); }
}
internal sealed class LeaseEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<LeaseEntity>(m, "Leases")
{
    public override void Configure(EntityTypeBuilder<LeaseEntity> b) { Map(b); b.HasKey(x => x.Id); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.ResourceKey).HasMaxLength(256).IsRequired(); b.Property(x => x.Owner).HasMaxLength(256).IsRequired(); b.Property(x => x.FencingVersion).IsConcurrencyToken(); b.HasIndex(x => new { x.TenantScope, x.ResourceKey }).IsUnique(); }
}
internal sealed class ApprovalEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<ApprovalEntity>(m, "Approvals")
{
    public override void Configure(EntityTypeBuilder<ApprovalEntity> b) { Map(b); b.HasKey(x => x.ApprovalId); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.PrincipalScope).HasMaxLength(256).IsRequired(); b.Property(x => x.State).HasMaxLength(32).IsRequired(); b.Property(x => x.BoundArgumentsHash).HasMaxLength(128); b.HasIndex(x => new { x.TenantScope, x.TaskId }); }
}
internal sealed class PrivacyTokenEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<PrivacyTokenEntity>(m, "PrivacyTokens")
{
    public override void Configure(EntityTypeBuilder<PrivacyTokenEntity> b) { Map(b); b.HasKey(x => x.Id); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.PrincipalOrWorkloadScope).HasMaxLength(256).IsRequired(); b.Property(x => x.TokenId).HasMaxLength(256).IsRequired(); b.Property(x => x.EntityType).HasMaxLength(128).IsRequired(); b.Property(x => x.KeyProviderId).HasMaxLength(128).IsRequired(); b.Property(x => x.KeyVersion).HasMaxLength(128).IsRequired(); b.Property(x => x.BindingDigest).HasMaxLength(128).IsRequired(); b.HasIndex(x => new { x.TenantScope, x.PrincipalOrWorkloadScope, x.TokenId, x.SessionId, x.TurnId }).IsUnique(); b.HasIndex(x => x.ExpiresUtc); }
}
internal sealed class OperationalRunEntityConfiguration(OpenConnectModelOptions m) : OpenConnectEntityConfiguration<OperationalRunEntity>(m, "OperationalRuns")
{
    public override void Configure(EntityTypeBuilder<OperationalRunEntity> b) { Map(b); b.HasKey(x => x.Id); b.Property(x => x.TenantScope).HasMaxLength(128).IsRequired(); b.Property(x => x.WorkloadScope).HasMaxLength(256).IsRequired(); b.Property(x => x.RunId).HasMaxLength(256).IsRequired(); b.Property(x => x.Status).HasMaxLength(32).IsRequired(); b.Property(x => x.FencingVersion).IsConcurrencyToken(); b.HasIndex(x => new { x.TenantScope, x.WorkloadScope, x.RunId }).IsUnique(); }
}
