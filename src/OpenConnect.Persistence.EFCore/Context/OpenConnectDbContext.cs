using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Messaging;
using OpenConnect.Persistence.EFCore.Model;

namespace OpenConnect.Persistence.EFCore.Context;

public sealed class OpenConnectDbContext(DbContextOptions<OpenConnectDbContext> options) : DbContext(options)
{
    public DbSet<MessagingConversationEntity> MessagingConversations => Set<MessagingConversationEntity>();
    public DbSet<MessagingParticipantEntity> MessagingParticipants => Set<MessagingParticipantEntity>();
    public DbSet<MessagingDeviceEntity> MessagingDevices => Set<MessagingDeviceEntity>();
    public DbSet<MessagingMessageEntity> MessagingMessages => Set<MessagingMessageEntity>();
    public DbSet<MessagingReceiptEntity> MessagingReceipts => Set<MessagingReceiptEntity>();
    public DbSet<MessagingKeyPackageEntity> MessagingKeyPackages => Set<MessagingKeyPackageEntity>();
    public DbSet<MessagingAttachmentEntity> MessagingAttachments => Set<MessagingAttachmentEntity>();
    public DbSet<MessagingCallEntity> MessagingCalls => Set<MessagingCallEntity>();
    public DbSet<MessagingFollowUpEntity> MessagingFollowUps => Set<MessagingFollowUpEntity>();
    public DbSet<MessagingContinuationEntity> MessagingContinuations => Set<MessagingContinuationEntity>();
    public DbSet<MessagingEnrollmentEntity> MessagingEnrollments => Set<MessagingEnrollmentEntity>();
    public DbSet<MessagingRefreshGrantEntity> MessagingRefreshGrants => Set<MessagingRefreshGrantEntity>();
    public DbSet<MessagingPushRegistrationEntity> MessagingPushRegistrations => Set<MessagingPushRegistrationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
        modelBuilder.ApplyOpenConnectMessagingModel();
    }
}
