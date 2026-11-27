using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using OpenConnect.Persistence.EFCore.Messaging;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Messaging;

/// <summary>
/// EF Core-backed event replay, conversation listing, and message history store using <see cref="BlazorDbContext"/>.
/// Strictly scoped by tenant and active participant membership.
/// </summary>
public sealed class TodoMessagingEventReplayStore(BlazorDbContext db) : IMessagingEventReplayStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        TenantId tenantId,
        PrincipalId principalId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var principalText = principalId.Value.ToString();
        var activeStatus = ParticipantStatus.Active.ToString();

        var conversationIds = await db.Set<MessagingParticipantEntity>()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantText && p.PrincipalId == principalText && p.Status == activeStatus)
            .Select(p => p.ConversationId)
            .Distinct()
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (conversationIds.Count == 0)
            return [];

        var entities = await db.Set<MessagingConversationEntity>()
            .AsNoTracking()
            .Include(c => c.Participants)
            .Where(c => c.TenantId == tenantText && conversationIds.Contains(c.ConversationId))
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(ToConversationRecord).ToArray();
    }

    public async ValueTask<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        TenantId tenantId,
        ConversationId conversationId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var convText = conversationId.Value.ToString();

        var rows = await db.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantText && m.ConversationId == convText && m.Sequence > afterSequence)
            .OrderBy(m => m.Sequence)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToMessageRecord).ToArray();
    }

    public async ValueTask<IReadOnlyList<MessagingEventEnvelope>> ReplayEventsAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId? deviceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var principalText = principalId.Value.ToString();
        var activeStatus = ParticipantStatus.Active.ToString();

        var conversationIds = await db.Set<MessagingParticipantEntity>()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantText && p.PrincipalId == principalText && p.Status == activeStatus)
            .Select(p => p.ConversationId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (conversationIds.Count == 0)
            return [];

        var messages = await db.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantText && conversationIds.Contains(m.ConversationId) && m.Sequence > afterSequence)
            .OrderBy(m => m.Sequence)
            .ThenBy(m => m.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return messages.Select(m =>
        {
            var convId = new ConversationId(Ulid.Parse(m.ConversationId));
            var record = ToMessageRecord(m);
            return new MessagingEventEnvelope(
                SchemaVersion: 1,
                EventId: $"msg-{m.MessageId}",
                Type: "messaging.message.committed",
                TenantId: tenantId,
                ConversationId: convId,
                Sequence: m.Sequence,
                Cursor: m.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OccurredAt: new DateTimeOffset(DateTime.SpecifyKind(m.CreatedAtUtc, DateTimeKind.Utc)),
                PayloadJson: JsonSerializer.Serialize(record, JsonOptions),
                CorrelationId: m.CorrelationId);
        }).ToArray();
    }

    private static ConversationRecord ToConversationRecord(MessagingConversationEntity entity) =>
        new(
            new TenantId(Ulid.Parse(entity.TenantId)),
            entity.ApplicationId,
            new ConversationId(Ulid.Parse(entity.ConversationId)),
            Enum.Parse<ConversationSecurityProfile>(entity.SecurityProfile),
            Enum.Parse<ConversationState>(entity.State),
            entity.MembershipVersion,
            entity.CryptoEpoch,
            entity.NextSequence,
            new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)),
            entity.Participants.Select(p => new ConversationParticipant(
                new ConversationId(Ulid.Parse(p.ConversationId)),
                Enum.Parse<ParticipantKind>(p.Kind),
                string.IsNullOrWhiteSpace(p.PrincipalId) ? null : new PrincipalId(Ulid.Parse(p.PrincipalId)),
                string.IsNullOrWhiteSpace(p.AgentId) ? null : new AgentId(p.AgentId),
                Enum.Parse<ParticipantRole>(p.Role),
                Enum.Parse<ParticipantStatus>(p.Status),
                p.JoinVersion,
                p.LeaveVersion,
                new DateTimeOffset(DateTime.SpecifyKind(p.JoinedAtUtc, DateTimeKind.Utc)))).ToArray());

    private static MessageRecord ToMessageRecord(MessagingMessageEntity entity) =>
        new(
            new TenantId(Ulid.Parse(entity.TenantId)),
            new ConversationId(Ulid.Parse(entity.ConversationId)),
            new MessageId(Ulid.Parse(entity.MessageId)),
            entity.Sequence,
            Enum.Parse<ParticipantKind>(entity.SenderKind),
            string.IsNullOrWhiteSpace(entity.SenderPrincipalId) ? null : new PrincipalId(Ulid.Parse(entity.SenderPrincipalId)),
            string.IsNullOrWhiteSpace(entity.SenderAgentId) ? null : new AgentId(entity.SenderAgentId),
            string.IsNullOrWhiteSpace(entity.SenderDeviceId) ? null : new DeviceId(Ulid.Parse(entity.SenderDeviceId)),
            new EncryptedMessageEnvelope(
                entity.Protocol,
                entity.CryptoEpoch,
                entity.CiphertextBase64,
                entity.NonceBase64,
                entity.AuthTagBase64,
                entity.AssociatedDataBase64,
                entity.SenderKeyId),
            entity.ClientOperationId,
            entity.PayloadDigestSha256,
            new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)),
            entity.CorrelationId);
}

/// <summary>EF Core-backed single-use device key package store.</summary>
public sealed class TodoMessagingKeyPackageStore(BlazorDbContext db) : IMessagingKeyPackageStore
{
    public async ValueTask<DeviceKeyPackage> PublishAsync(DeviceKeyPackage keyPackage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPackage);
        var entity = new MessagingKeyPackageEntity
        {
            TenantId = keyPackage.TenantId.Value.ToString(),
            ApplicationId = TodoDirectorySeed.ApplicationId,
            KeyPackageId = keyPackage.KeyPackageId.Value.ToString(),
            OwnerPrincipalId = keyPackage.OwnerPrincipalId?.Value.ToString(),
            OwnerAgentId = keyPackage.OwnerAgentId?.Value,
            DeviceId = keyPackage.DeviceId.Value.ToString(),
            Protocol = keyPackage.Protocol,
            PublicPackageBase64 = keyPackage.PublicPackageBase64,
            CreatedAtUtc = keyPackage.CreatedAt.UtcDateTime,
            ExpiresAtUtc = keyPackage.ExpiresAt.UtcDateTime,
            Consumed = false
        };
        db.Set<MessagingKeyPackageEntity>().Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return keyPackage;
    }

    public async ValueTask<DeviceKeyPackage?> ConsumeOneAsync(
        TenantId tenantId,
        PrincipalId? ownerPrincipalId,
        AgentId? ownerAgentId,
        DeviceId? deviceId,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var now = DateTime.UtcNow;
        var query = db.Set<MessagingKeyPackageEntity>()
            .Where(k => k.TenantId == tenantText && !k.Consumed && k.ExpiresAtUtc > now);

        if (ownerPrincipalId.HasValue)
        {
            var principalText = ownerPrincipalId.Value.Value.ToString();
            query = query.Where(k => k.OwnerPrincipalId == principalText);
        }
        if (ownerAgentId.HasValue)
        {
            var agentText = ownerAgentId.Value.Value;
            query = query.Where(k => k.OwnerAgentId == agentText);
        }
        if (deviceId.HasValue)
        {
            var devText = deviceId.Value.Value.ToString();
            query = query.Where(k => k.DeviceId == devText);
        }

        var entity = await query.OrderBy(k => k.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return null;

        entity.Consumed = true;
        entity.ConsumedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new DeviceKeyPackage(
            tenantId,
            new KeyPackageId(Ulid.Parse(entity.KeyPackageId)),
            string.IsNullOrWhiteSpace(entity.OwnerPrincipalId) ? null : new PrincipalId(Ulid.Parse(entity.OwnerPrincipalId)),
            string.IsNullOrWhiteSpace(entity.OwnerAgentId) ? null : new AgentId(entity.OwnerAgentId),
            new DeviceId(Ulid.Parse(entity.DeviceId)),
            entity.Protocol,
            entity.PublicPackageBase64,
            new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(entity.ExpiresAtUtc, DateTimeKind.Utc)),
            Consumed: true,
            ConsumedAt: new DateTimeOffset(now, TimeSpan.Zero));
    }
}

/// <summary>EF Core-backed push notification registration store (stores protected token handles only).</summary>
public sealed class TodoMessagingPushRegistrationStore(BlazorDbContext db) : IMessagingPushRegistrationStore
{
    public async ValueTask<PushRegistrationRecord> RegisterAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        PushProviderKind provider,
        string protectedTokenHandle,
        bool consentGranted,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var regId = new PushRegistrationId(Ulid.NewUlid());
        var entity = new MessagingPushRegistrationEntity
        {
            TenantId = tenantId.Value.ToString(),
            ApplicationId = applicationId,
            RegistrationId = regId.Value.ToString(),
            PrincipalId = principalId.Value.ToString(),
            DeviceId = deviceId.Value.ToString(),
            Provider = provider.ToString(),
            ProtectedTokenHandle = protectedTokenHandle,
            ConsentGranted = consentGranted,
            Status = PushRegistrationStatus.Active.ToString(),
            UpdatedAtUtc = now.UtcDateTime
        };
        db.Set<MessagingPushRegistrationEntity>().Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PushRegistrationRecord(
            tenantId,
            applicationId,
            regId,
            principalId,
            deviceId,
            provider,
            protectedTokenHandle,
            consentGranted,
            PushRegistrationStatus.Active,
            now);
    }

    public async ValueTask<bool> RevokeAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId deviceId,
        PushProviderKind? provider = null,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var principalText = principalId.Value.ToString();
        var deviceText = deviceId.Value.ToString();
        var query = db.Set<MessagingPushRegistrationEntity>()
            .Where(r => r.TenantId == tenantText && r.PrincipalId == principalText && r.DeviceId == deviceText);
        if (provider.HasValue)
        {
            var providerText = provider.Value.ToString();
            query = query.Where(r => r.Provider == providerText);
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
            return false;

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.Status = PushRegistrationStatus.Revoked.ToString();
            row.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>EF Core metadata + encrypted blob store for sample messaging attachments.</summary>
public sealed class TodoMessagingAttachmentStore(BlazorDbContext db) : IMessagingAttachmentStore
{
    private static readonly ConcurrentDictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);

    public async ValueTask<AttachmentRecord> InitUploadAsync(
        TenantId tenantId,
        ConversationId conversationId,
        long encryptedSizeBytes,
        string encryptedDigestSha256,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var attId = new AttachmentId(Ulid.NewUlid());
        var objectKey = $"{tenantId.Value}/{conversationId.Value}/{attId.Value}.enc";
        var entity = new MessagingAttachmentEntity
        {
            TenantId = tenantId.Value.ToString(),
            ApplicationId = TodoDirectorySeed.ApplicationId,
            AttachmentId = attId.Value.ToString(),
            ConversationId = conversationId.Value.ToString(),
            ObjectKey = objectKey,
            ObjectVersion = "v1",
            EncryptedSizeBytes = encryptedSizeBytes,
            EncryptedDigestSha256 = encryptedDigestSha256,
            State = AttachmentState.Reserved.ToString(),
            CreatedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = now.Add(expiry).UtcDateTime
        };
        db.Set<MessagingAttachmentEntity>().Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async ValueTask<AttachmentRecord> CompleteUploadAsync(
        TenantId tenantId,
        AttachmentId attachmentId,
        ReadOnlyMemory<byte> encryptedBytes,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var attText = attachmentId.Value.ToString();
        var entity = await db.Set<MessagingAttachmentEntity>()
            .SingleOrDefaultAsync(a => a.TenantId == tenantText && a.AttachmentId == attText, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Attachment reservation not found.", 404);

        var actualDigest = Convert.ToHexString(SHA256.HashData(encryptedBytes.Span)).ToLowerInvariant();
        if (!string.Equals(actualDigest, entity.EncryptedDigestSha256, StringComparison.OrdinalIgnoreCase))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Attachment digest does not match reserved digest.", 400);

        Blobs[entity.ObjectKey] = encryptedBytes.ToArray();
        entity.State = AttachmentState.Committed.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async ValueTask<MessagingAttachmentDownload?> DownloadAsync(
        TenantId tenantId,
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        var tenantText = tenantId.Value.ToString();
        var attText = attachmentId.Value.ToString();
        var entity = await db.Set<MessagingAttachmentEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantId == tenantText && a.AttachmentId == attText, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null || !Blobs.TryGetValue(entity.ObjectKey, out var bytes))
            return null;

        return new MessagingAttachmentDownload(ToRecord(entity), bytes);
    }

    private static AttachmentRecord ToRecord(MessagingAttachmentEntity entity) =>
        new(
            new TenantId(Ulid.Parse(entity.TenantId)),
            new AttachmentId(Ulid.Parse(entity.AttachmentId)),
            new ConversationId(Ulid.Parse(entity.ConversationId)),
            entity.ObjectKey,
            entity.ObjectVersion,
            entity.EncryptedSizeBytes,
            entity.EncryptedDigestSha256,
            Enum.Parse<AttachmentState>(entity.State),
            new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(entity.ExpiresAtUtc, DateTimeKind.Utc)));
}
