using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.Messaging;

internal abstract class MessagingEntityConfiguration<TEntity>(OpenConnectModelOptions mapping, string table)
    : IEntityTypeConfiguration<TEntity> where TEntity : class
{
    protected void Map(EntityTypeBuilder<TEntity> builder) => builder.ToTable(
        mapping.UseSqlitePrefix ? mapping.SqliteTablePrefix + table.ToLowerInvariant() : table,
        mapping.UseSqlitePrefix ? null : mapping.Schema);

    public abstract void Configure(EntityTypeBuilder<TEntity> builder);
}

internal sealed class MessagingConversationEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingConversationEntity>(m, "MessagingConversations")
{
    public override void Configure(EntityTypeBuilder<MessagingConversationEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.SecurityProfile).HasMaxLength(32).IsRequired();
        b.Property(x => x.State).HasMaxLength(32).IsRequired();
        b.Property(x => x.MembershipVersion).IsConcurrencyToken();
        b.Property(x => x.NextSequence).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.ConversationId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.State });
    }
}

internal sealed class MessagingParticipantEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingParticipantEntity>(m, "MessagingParticipants")
{
    public override void Configure(EntityTypeBuilder<MessagingParticipantEntity> b)
    {
        Map(b);
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        b.Property(x => x.PrincipalId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.AgentId).HasMaxLength(128).IsRequired(false);
        b.Property(x => x.ParticipantKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.Role).HasMaxLength(32).IsRequired();
        b.Property(x => x.Status).HasMaxLength(32).IsRequired();
        b.HasOne(x => x.Conversation)
            .WithMany(c => c.Participants)
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.ParticipantKey }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.PrincipalId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.AgentId, x.Status });
    }
}

internal sealed class MessagingDeviceEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingDeviceEntity>(m, "MessagingDevices")
{
    public override void Configure(EntityTypeBuilder<MessagingDeviceEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.DeviceId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Platform).HasMaxLength(32).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        b.Property(x => x.PublicKeyBase64).HasMaxLength(2048).IsRequired();
        b.Property(x => x.KeyFingerprint).HasMaxLength(128).IsRequired();
        b.Property(x => x.Status).HasMaxLength(32).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.DeviceId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.PrincipalId, x.Status });
    }
}

internal sealed class MessagingMessageEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingMessageEntity>(m, "MessagingMessages")
{
    public override void Configure(EntityTypeBuilder<MessagingMessageEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.MessageId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.MessageId).HasMaxLength(64).IsRequired();
        b.Property(x => x.SenderKind).HasMaxLength(32).IsRequired();
        b.Property(x => x.SenderPrincipalId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.SenderAgentId).HasMaxLength(128).IsRequired(false);
        b.Property(x => x.SenderDeviceId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.SenderKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.Protocol).HasMaxLength(64).IsRequired();
        b.Property(x => x.CiphertextBase64).IsRequired();
        b.Property(x => x.NonceBase64).HasMaxLength(256).IsRequired();
        b.Property(x => x.AuthTagBase64).HasMaxLength(256).IsRequired();
        b.Property(x => x.AssociatedDataBase64).HasMaxLength(2048).IsRequired(false);
        b.Property(x => x.SenderKeyId).HasMaxLength(256).IsRequired(false);
        b.Property(x => x.ClientOperationId).HasMaxLength(256).IsRequired();
        b.Property(x => x.PayloadDigestSha256).HasMaxLength(128).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired(false);
        b.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.SenderKey, x.ClientOperationId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ConversationId, x.ClientOperationId });
        b.HasIndex(x => new { x.TenantId, x.ConversationId, x.MessageId }).IsUnique();
    }
}

internal sealed class MessagingReceiptEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingReceiptEntity>(m, "MessagingReceipts")
{
    public override void Configure(EntityTypeBuilder<MessagingReceiptEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.MessageId, x.DeviceId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.MessageId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        b.Property(x => x.FailureReason).HasMaxLength(512).IsRequired(false);
        b.HasOne(x => x.Message)
            .WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.MessageId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.MessageId, x.DeviceId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.PrincipalId });
    }
}

internal sealed class MessagingKeyPackageEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingKeyPackageEntity>(m, "MessagingKeyPackages")
{
    public override void Configure(EntityTypeBuilder<MessagingKeyPackageEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.KeyPackageId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.KeyPackageId).HasMaxLength(64).IsRequired();
        b.Property(x => x.OwnerPrincipalId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.OwnerAgentId).HasMaxLength(128).IsRequired(false);
        b.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Protocol).HasMaxLength(64).IsRequired();
        b.Property(x => x.PublicPackageBase64).IsRequired();
        b.Property(x => x.Consumed).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.DeviceId, x.Consumed, x.ExpiresAtUtc });
    }
}

internal sealed class MessagingAttachmentEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingAttachmentEntity>(m, "MessagingAttachments")
{
    public override void Configure(EntityTypeBuilder<MessagingAttachmentEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.AttachmentId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.AttachmentId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
        b.Property(x => x.ObjectVersion).HasMaxLength(128).IsRequired();
        b.Property(x => x.EncryptedDigestSha256).HasMaxLength(128).IsRequired();
        b.Property(x => x.State).HasMaxLength(32).IsRequired();
        b.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ObjectKey, x.ObjectVersion }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.State });
    }
}

internal sealed class MessagingCallEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingCallEntity>(m, "MessagingCalls")
{
    public override void Configure(EntityTypeBuilder<MessagingCallEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.CallId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.CallId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.CallerKind).HasMaxLength(32).IsRequired();
        b.Property(x => x.CallerPrincipalId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.CallerAgentId).HasMaxLength(128).IsRequired(false);
        b.Property(x => x.AllowedHumanRecipientsJson).IsRequired();
        b.Property(x => x.State).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        b.Property(x => x.MediaSecurityMode).HasMaxLength(32).IsRequired();
        b.Property(x => x.AcceptedDeviceId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.AcceptedPrincipalId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.VoiceSessionId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.AdmissionTokenHash).HasMaxLength(128).IsRequired(false);
        b.Property(x => x.EndReason).HasMaxLength(256).IsRequired(false);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.CallId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.State });
    }
}

internal sealed class MessagingFollowUpEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingFollowUpEntity>(m, "MessagingFollowUps")
{
    public override void Configure(EntityTypeBuilder<MessagingFollowUpEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.FollowUpId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.FollowUpId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.TaskId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.AgentId).HasMaxLength(128).IsRequired();
        b.Property(x => x.RecipientPrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.State).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        b.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
        b.Property(x => x.CausalEventId).HasMaxLength(256).IsRequired();
        b.Property(x => x.LeaseOwner).HasMaxLength(256).IsRequired(false);
        b.Property(x => x.LeaseFence).IsConcurrencyToken();
        b.Property(x => x.QuietHoursStartUtc).HasMaxLength(16).IsRequired(false);
        b.Property(x => x.QuietHoursEndUtc).HasMaxLength(16).IsRequired(false);
        b.Property(x => x.TimeZoneId).HasMaxLength(128).IsRequired(false);
        b.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.FollowUpId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.State, x.DueAtUtc, x.LeaseExpiresAtUtc });
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.RecipientPrincipalId, x.State });
    }
}

internal sealed class MessagingContinuationEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingContinuationEntity>(m, "MessagingContinuations")
{
    public override void Configure(EntityTypeBuilder<MessagingContinuationEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.ContinuationId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ContinuationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.TriggeringEventId).HasMaxLength(256).IsRequired();
        b.Property(x => x.AgentId).HasMaxLength(128).IsRequired();
        b.Property(x => x.RecipientPrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.TaskId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.SessionId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.Status).HasMaxLength(32).IsRequired();
        b.Property(x => x.ExecutionIdempotencyKey).HasMaxLength(256).IsRequired();
        b.Property(x => x.LeaseOwner).HasMaxLength(256).IsRequired(false);
        b.Property(x => x.LeaseFence).IsConcurrencyToken();
        b.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => new { x.TenantId, x.ApplicationId, x.ConversationId })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ExecutionIdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.Status });
    }
}

internal sealed class MessagingEnrollmentEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingEnrollmentEntity>(m, "MessagingEnrollments")
{
    public override void Configure(EntityTypeBuilder<MessagingEnrollmentEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.EnrollmentId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.EnrollmentId).HasMaxLength(64).IsRequired();
        b.Property(x => x.InitiatingPrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Issuer).HasMaxLength(256).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        b.Property(x => x.TokenHashSha256).HasMaxLength(128).IsRequired();
        b.Property(x => x.VerificationCode).HasMaxLength(32).IsRequired();
        b.Property(x => x.State).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        b.Property(x => x.ClaimedDeviceId).HasMaxLength(64).IsRequired(false);
        b.Property(x => x.ClaimedDevicePublicKeyBase64).HasMaxLength(2048).IsRequired(false);
        b.Property(x => x.ClaimedDeviceName).HasMaxLength(256).IsRequired(false);
        b.Property(x => x.ClaimedPlatform).HasMaxLength(32).IsRequired(false);
        b.Property(x => x.ChallengeNonceBase64).HasMaxLength(256).IsRequired(false);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.EnrollmentId }).IsUnique();
        b.HasIndex(x => x.TokenHashSha256).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.InitiatingPrincipalId, x.State });
    }
}

internal sealed class MessagingRefreshGrantEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingRefreshGrantEntity>(m, "MessagingRefreshGrants")
{
    public override void Configure(EntityTypeBuilder<MessagingRefreshGrantEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.GrantId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.GrantId).HasMaxLength(64).IsRequired();
        b.Property(x => x.FamilyId).HasMaxLength(128).IsRequired();
        b.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Issuer).HasMaxLength(256).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        b.Property(x => x.Audience).HasMaxLength(256).IsRequired();
        b.Property(x => x.TokenHashSha256).HasMaxLength(128).IsRequired();
        b.Property(x => x.Status).HasMaxLength(32).IsRequired().IsConcurrencyToken();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.TokenHashSha256).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.FamilyId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.DeviceId, x.Status });
    }
}

internal sealed class MessagingPushRegistrationEntityConfiguration(OpenConnectModelOptions m)
    : MessagingEntityConfiguration<MessagingPushRegistrationEntity>(m, "MessagingPushRegistrations")
{
    public override void Configure(EntityTypeBuilder<MessagingPushRegistrationEntity> b)
    {
        Map(b);
        b.HasKey(x => new { x.TenantId, x.ApplicationId, x.RegistrationId });
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ApplicationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.RegistrationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PrincipalId).HasMaxLength(64).IsRequired();
        b.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(32).IsRequired();
        b.Property(x => x.ProtectedTokenHandle).HasMaxLength(1024).IsRequired();
        b.Property(x => x.Status).HasMaxLength(32).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.DeviceId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.ApplicationId, x.PrincipalId, x.Status });
    }
}

public static class MessagingModelBuilderExtensions
{
    public static ModelBuilder ApplyOpenConnectMessagingModel(
        this ModelBuilder modelBuilder,
        OpenConnectModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var mapping = options ?? OpenConnectModelOptions.Default;
        modelBuilder.ApplyConfiguration(new MessagingConversationEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingParticipantEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingDeviceEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingMessageEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingReceiptEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingKeyPackageEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingAttachmentEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingCallEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingFollowUpEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingContinuationEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingEnrollmentEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingRefreshGrantEntityConfiguration(mapping));
        modelBuilder.ApplyConfiguration(new MessagingPushRegistrationEntityConfiguration(mapping));
        return modelBuilder;
    }
}
