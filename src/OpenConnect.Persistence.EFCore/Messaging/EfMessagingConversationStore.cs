using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Messaging;

public sealed class EfMessagingConversationStore<TContext>(TContext context)
    : IMessagingConversationStore
    where TContext : DbContext
{
    public async ValueTask<ConversationRecord> CreateConversationAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var applicationId = MessagingPersistenceHelpers.RequireApplicationId(conversation.ApplicationId);
        var tenantId = MessagingPersistenceHelpers.Scope(conversation.TenantId);
        var conversationId = MessagingPersistenceHelpers.Scope(conversation.ConversationId);

        var hasActiveHuman = conversation.Participants.Any(p =>
            p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active && p.PrincipalId.HasValue);
        if (!hasActiveHuman)
            throw new MessagingDomainException("invalid_request", "Every conversation must include at least one active human participant.", 400);

        if (conversation.SecurityProfile == ConversationSecurityProfile.PrivateE2EE &&
            conversation.Participants.Any(p => p.Kind == ParticipantKind.Agent))
        {
            throw new MessagingDomainException("invalid_request", "PrivateE2EE conversations cannot include agent participants.", 400);
        }

        await using var tx = await MessagingPersistenceHelpers.BeginIfNeededAsync(context, cancellationToken).ConfigureAwait(false);

        var entity = new MessagingConversationEntity
        {
            TenantId = tenantId,
            ApplicationId = applicationId,
            ConversationId = conversationId,
            SecurityProfile = conversation.SecurityProfile.ToString(),
            State = conversation.State.ToString(),
            MembershipVersion = conversation.MembershipVersion,
            CryptoEpoch = conversation.CryptoEpoch,
            NextSequence = conversation.NextSequence,
            CreatedAtUtc = MessagingPersistenceHelpers.Utc(conversation.CreatedAt == default ? DateTimeOffset.UtcNow : conversation.CreatedAt)
        };

        foreach (var participant in conversation.Participants)
        {
            entity.Participants.Add(new MessagingParticipantEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ApplicationId = applicationId,
                ConversationId = conversationId,
                Kind = participant.Kind.ToString(),
                PrincipalId = participant.PrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(participant.PrincipalId.Value) : null,
                AgentId = participant.AgentId.HasValue ? MessagingPersistenceHelpers.Scope(participant.AgentId.Value) : null,
                ParticipantKey = MessagingPersistenceHelpers.BuildParticipantKey(participant.Kind, participant.PrincipalId, participant.AgentId),
                Role = participant.Role.ToString(),
                Status = participant.Status.ToString(),
                JoinVersion = participant.JoinVersion,
                LeaveVersion = participant.LeaveVersion,
                JoinedAtUtc = MessagingPersistenceHelpers.Utc(participant.JoinedAt == default ? DateTimeOffset.UtcNow : participant.JoinedAt)
            });
        }

        context.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Conversation already exists or violates uniqueness constraints.", 409, ex);
        }

        if (tx is not null)
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        context.ChangeTracker.Clear();
        return MessagingPersistenceHelpers.ToRecord(entity);
    }

    public async ValueTask<ConversationRecord?> GetConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversationId);

        var entity = await context.Set<MessagingConversationEntity>()
            .AsNoTracking()
            .Include(c => c.Participants)
            .SingleOrDefaultAsync(c => c.TenantId == tenant && c.ConversationId == conv, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : MessagingPersistenceHelpers.ToRecord(entity);
    }

    public async ValueTask<ConversationRecord> UpdateConversationAsync(
        ConversationRecord conversation,
        long expectedMembershipVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var tenant = MessagingPersistenceHelpers.Scope(conversation.TenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversation.ConversationId);

        await using var tx = await MessagingPersistenceHelpers.BeginIfNeededAsync(context, cancellationToken).ConfigureAwait(false);

        var existing = await context.Set<MessagingConversationEntity>()
            .Include(c => c.Participants)
            .SingleOrDefaultAsync(c => c.TenantId == tenant && c.ConversationId == conv, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
            throw new MessagingDomainException("not_found", "Conversation not found.", 404);

        if (existing.MembershipVersion != expectedMembershipVersion)
            throw new MessagingDomainException("conflict", "Conversation membership version conflict.", 409);

        var existingProfile = Enum.Parse<ConversationSecurityProfile>(existing.SecurityProfile);
        if (existingProfile != conversation.SecurityProfile)
            throw new MessagingDomainException("conflict", "Conversation security profile is immutable.", 409);

        if (existingProfile == ConversationSecurityProfile.PrivateE2EE &&
            conversation.Participants.Any(p => p.Kind == ParticipantKind.Agent && p.Status == ParticipantStatus.Active))
        {
            throw new MessagingDomainException("invalid_request", "PrivateE2EE conversations cannot include agent participants.", 400);
        }

        var hasActiveHuman = conversation.Participants.Any(p =>
            p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active && p.PrincipalId.HasValue);

        var targetState = hasActiveHuman ? conversation.State : ConversationState.Closed;
        var nextMembershipVersion = conversation.MembershipVersion > expectedMembershipVersion
            ? conversation.MembershipVersion
            : expectedMembershipVersion + 1;

        existing.State = targetState.ToString();
        existing.MembershipVersion = nextMembershipVersion;
        existing.CryptoEpoch = conversation.CryptoEpoch;
        existing.NextSequence = Math.Max(existing.NextSequence, conversation.NextSequence);

        var existingByKey = existing.Participants.ToDictionary(p => p.ParticipantKey, StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var participant in conversation.Participants)
        {
            var key = MessagingPersistenceHelpers.BuildParticipantKey(participant.Kind, participant.PrincipalId, participant.AgentId);
            seenKeys.Add(key);

            if (existingByKey.TryGetValue(key, out var row))
            {
                row.Role = participant.Role.ToString();
                row.Status = participant.Status.ToString();
                row.JoinVersion = participant.JoinVersion;
                row.LeaveVersion = participant.LeaveVersion;
            }
            else
            {
                existing.Participants.Add(new MessagingParticipantEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = existing.TenantId,
                    ApplicationId = existing.ApplicationId,
                    ConversationId = existing.ConversationId,
                    Kind = participant.Kind.ToString(),
                    PrincipalId = participant.PrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(participant.PrincipalId.Value) : null,
                    AgentId = participant.AgentId.HasValue ? MessagingPersistenceHelpers.Scope(participant.AgentId.Value) : null,
                    ParticipantKey = key,
                    Role = participant.Role.ToString(),
                    Status = participant.Status.ToString(),
                    JoinVersion = participant.JoinVersion,
                    LeaveVersion = participant.LeaveVersion,
                    JoinedAtUtc = MessagingPersistenceHelpers.Utc(participant.JoinedAt == default ? DateTimeOffset.UtcNow : participant.JoinedAt)
                });
            }
        }

        foreach (var stale in existing.Participants.Where(p => !seenKeys.Contains(p.ParticipantKey)).ToArray())
        {
            context.Remove(stale);
        }

        var nowUtc = DateTime.UtcNow;

        if (!hasActiveHuman)
        {
            await context.Set<MessagingCallEntity>()
                .Where(c => c.TenantId == existing.TenantId
                    && c.ApplicationId == existing.ApplicationId
                    && c.ConversationId == existing.ConversationId
                    && MessagingPersistenceHelpers.ActiveCallStates.Contains(c.State))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.State, nameof(CallState.Ended))
                    .SetProperty(c => c.EndedAtUtc, nowUtc)
                    .SetProperty(c => c.EndReason, "last_human_removed")
                    .SetProperty(c => c.Version, c => c.Version + 1),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var membershipEventId = $"membership-{existing.ConversationId}-v{existing.MembershipVersion}";
        var membershipPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventId = membershipEventId,
            type = MessagingEventTypes.MembershipChanged,
            conversationId = existing.ConversationId,
            membershipVersion = existing.MembershipVersion,
            cryptoEpoch = existing.CryptoEpoch,
            state = existing.State,
            occurredAt = nowUtc
        });

        context.Add(new OutboxEntity
        {
            Id = Guid.NewGuid(),
            TenantScope = existing.TenantId,
            EventId = membershipEventId,
            Subject = MessagingPersistenceHelpers.BuildDurableSubject(existing.TenantId, existing.ConversationId),
            SanitizedPayload = membershipPayload,
            Status = "pending",
            FencingVersion = 0,
            CreatedUtc = nowUtc,
            NextAttemptUtc = nowUtc,
            Attempts = 0
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Conversation membership version conflict.", 409, ex);
        }

        if (tx is not null)
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        var result = MessagingPersistenceHelpers.ToRecord(existing);
        context.ChangeTracker.Clear();
        return result;
    }

    public async ValueTask<MessageRecord?> GetMessageByClientOperationIdAsync(
        TenantId tenantId,
        ConversationId conversationId,
        string clientOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversationId);

        var entity = await context.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.TenantId == tenant && m.ConversationId == conv && m.ClientOperationId == clientOperationId,
                cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : MessagingPersistenceHelpers.ToRecord(entity);
    }

    public async ValueTask<MessageRecord> CommitMessageAsync(
        MessageRecord message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.ClientOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.PayloadDigestSha256);

        var tenant = MessagingPersistenceHelpers.Scope(message.TenantId);
        var conv = MessagingPersistenceHelpers.Scope(message.ConversationId);
        var senderKey = MessagingPersistenceHelpers.BuildParticipantKey(
            message.SenderKind,
            message.SenderPrincipalId,
            message.SenderAgentId);

        await using var tx = await MessagingPersistenceHelpers.BeginIfNeededAsync(context, cancellationToken).ConfigureAwait(false);

        var conversation = await context.Set<MessagingConversationEntity>()
            .Include(c => c.Participants)
            .SingleOrDefaultAsync(c => c.TenantId == tenant && c.ConversationId == conv, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
            throw new MessagingDomainException("not_found", "Conversation not found.", 404);

        var existingByOp = await context.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                m => m.TenantId == tenant
                    && m.ApplicationId == conversation.ApplicationId
                    && m.ConversationId == conv
                    && m.SenderKey == senderKey
                    && m.ClientOperationId == message.ClientOperationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existingByOp is not null)
        {
            if (!string.Equals(existingByOp.PayloadDigestSha256, message.PayloadDigestSha256, StringComparison.Ordinal))
            {
                context.ChangeTracker.Clear();
                throw new MessagingDomainException(
                    "conflict",
                    "Client operation ID was already committed with a different payload digest.",
                    409);
            }

            if (tx is not null)
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            context.ChangeTracker.Clear();
            return MessagingPersistenceHelpers.ToRecord(existingByOp);
        }

        if (!string.Equals(conversation.State, nameof(ConversationState.Active), StringComparison.Ordinal))
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Cannot commit a message to a non-active conversation.", 409);
        }

        var hasActiveHuman = conversation.Participants.Any(p =>
            string.Equals(p.Kind, nameof(ParticipantKind.Human), StringComparison.Ordinal)
            && string.Equals(p.Status, nameof(ParticipantStatus.Active), StringComparison.Ordinal));
        if (!hasActiveHuman)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Conversation has no active human participant.", 409);
        }

        var assignedSequence = conversation.NextSequence <= 0 ? 1L : conversation.NextSequence;
        conversation.NextSequence = assignedSequence + 1;

        var nowUtc = MessagingPersistenceHelpers.Utc(message.CreatedAt == default ? DateTimeOffset.UtcNow : message.CreatedAt);
        var messageId = MessagingPersistenceHelpers.Scope(message.MessageId);

        var messageEntity = new MessagingMessageEntity
        {
            TenantId = conversation.TenantId,
            ApplicationId = conversation.ApplicationId,
            ConversationId = conversation.ConversationId,
            MessageId = messageId,
            Sequence = assignedSequence,
            SenderKind = message.SenderKind.ToString(),
            SenderPrincipalId = message.SenderPrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(message.SenderPrincipalId.Value) : null,
            SenderAgentId = message.SenderAgentId.HasValue ? MessagingPersistenceHelpers.Scope(message.SenderAgentId.Value) : null,
            SenderDeviceId = message.SenderDeviceId.HasValue ? MessagingPersistenceHelpers.Scope(message.SenderDeviceId.Value) : null,
            SenderKey = senderKey,
            Protocol = message.Envelope.Protocol,
            CryptoEpoch = message.Envelope.CryptoEpoch,
            CiphertextBase64 = message.Envelope.CiphertextBase64,
            NonceBase64 = message.Envelope.NonceBase64,
            AuthTagBase64 = message.Envelope.AuthTagBase64,
            AssociatedDataBase64 = message.Envelope.AssociatedDataBase64,
            SenderKeyId = message.Envelope.SenderKeyId,
            ClientOperationId = message.ClientOperationId,
            PayloadDigestSha256 = message.PayloadDigestSha256,
            CorrelationId = message.CorrelationId,
            CreatedAtUtc = nowUtc
        };

        var eventId = $"msg-{messageId}";
        var outboxPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventId,
            type = MessagingEventTypes.MessageCommitted,
            conversationId = conversation.ConversationId,
            sequence = assignedSequence,
            occurredAt = nowUtc,
            payload = new
            {
                messageId,
                protocol = message.Envelope.Protocol,
                epoch = message.Envelope.CryptoEpoch,
                ciphertext = message.Envelope.CiphertextBase64,
                nonce = message.Envelope.NonceBase64,
                authTag = message.Envelope.AuthTagBase64,
                payloadDigestSha256 = message.PayloadDigestSha256
            }
        });

        context.Add(messageEntity);
        context.Add(new OutboxEntity
        {
            Id = Guid.NewGuid(),
            TenantScope = conversation.TenantId,
            EventId = eventId,
            Subject = MessagingPersistenceHelpers.BuildDurableSubject(conversation.TenantId, conversation.ConversationId),
            SanitizedPayload = outboxPayload,
            Status = "pending",
            FencingVersion = 0,
            CreatedUtc = nowUtc,
            NextAttemptUtc = nowUtc,
            Attempts = 0
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Concurrent message sequence allocation conflict.", 409, ex);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Message commit violated uniqueness constraints.", 409, ex);
        }

        if (tx is not null)
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        context.ChangeTracker.Clear();
        return MessagingPersistenceHelpers.ToRecord(messageEntity);
    }

    public async ValueTask<MessageRecord?> GetMessageAsync(
        TenantId tenantId,
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversationId);
        var msg = MessagingPersistenceHelpers.Scope(messageId);

        var entity = await context.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                m => m.TenantId == tenant && m.ConversationId == conv && m.MessageId == msg,
                cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : MessagingPersistenceHelpers.ToRecord(entity);
    }

    public async ValueTask<MessageReceiptRecord?> GetReceiptAsync(
        TenantId tenantId,
        MessageId messageId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var msg = MessagingPersistenceHelpers.Scope(messageId);
        var dev = MessagingPersistenceHelpers.Scope(deviceId);

        var entity = await context.Set<MessagingReceiptEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                r => r.TenantId == tenant && r.MessageId == msg && r.DeviceId == dev,
                cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : MessagingPersistenceHelpers.ToRecord(entity);
    }

    public async ValueTask<MessageReceiptRecord> UpsertReceiptAsync(
        MessageReceiptRecord receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var tenant = MessagingPersistenceHelpers.Scope(receipt.TenantId);
        var conv = MessagingPersistenceHelpers.Scope(receipt.ConversationId);
        var msg = MessagingPersistenceHelpers.Scope(receipt.MessageId);
        var dev = MessagingPersistenceHelpers.Scope(receipt.DeviceId);
        var principal = MessagingPersistenceHelpers.Scope(receipt.PrincipalId);

        await using var tx = await MessagingPersistenceHelpers.BeginIfNeededAsync(context, cancellationToken).ConfigureAwait(false);

        var messageEntity = await context.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                m => m.TenantId == tenant && m.ConversationId == conv && m.MessageId == msg,
                cancellationToken)
            .ConfigureAwait(false);

        if (messageEntity is null)
            throw new MessagingDomainException("not_found", "Message not found for receipt.", 404);

        var incomingRank = MessagingPersistenceHelpers.ReceiptRank(receipt.Kind);
        var recordedAtUtc = MessagingPersistenceHelpers.Utc(receipt.RecordedAt == default ? DateTimeOffset.UtcNow : receipt.RecordedAt);

        var existing = await context.Set<MessagingReceiptEntity>()
            .SingleOrDefaultAsync(
                r => r.TenantId == tenant
                    && r.ApplicationId == messageEntity.ApplicationId
                    && r.MessageId == msg
                    && r.DeviceId == dev,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var created = new MessagingReceiptEntity
            {
                TenantId = tenant,
                ApplicationId = messageEntity.ApplicationId,
                ConversationId = conv,
                MessageId = msg,
                PrincipalId = principal,
                DeviceId = dev,
                Kind = receipt.Kind.ToString(),
                Rank = incomingRank,
                Sequence = receipt.Sequence > 0 ? receipt.Sequence : messageEntity.Sequence,
                RecordedAtUtc = recordedAtUtc,
                DeliveredAtUtc = receipt.Kind is MessageReceiptKind.Delivered or MessageReceiptKind.Read ? recordedAtUtc : null,
                ReadAtUtc = receipt.Kind == MessageReceiptKind.Read ? recordedAtUtc : null,
                FailureReason = receipt.Kind == MessageReceiptKind.Failed ? receipt.FailureReason : null
            };
            context.Add(created);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (tx is not null)
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return MessagingPersistenceHelpers.ToRecord(created);
        }

        if (receipt.Kind == MessageReceiptKind.Failed)
        {
            if (existing.Rank < MessagingPersistenceHelpers.ReceiptRank(MessageReceiptKind.Delivered))
            {
                existing.Kind = nameof(MessageReceiptKind.Failed);
                existing.Rank = 0;
                existing.RecordedAtUtc = recordedAtUtc;
                existing.FailureReason = receipt.FailureReason;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else if (incomingRank > existing.Rank)
        {
            existing.Kind = receipt.Kind.ToString();
            existing.Rank = incomingRank;
            existing.Sequence = Math.Max(existing.Sequence, receipt.Sequence);
            existing.RecordedAtUtc = recordedAtUtc;
            existing.FailureReason = null;
            if (receipt.Kind is MessageReceiptKind.Delivered or MessageReceiptKind.Read)
                existing.DeliveredAtUtc ??= recordedAtUtc;
            if (receipt.Kind == MessageReceiptKind.Read)
                existing.ReadAtUtc ??= recordedAtUtc;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (tx is not null)
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        var result = MessagingPersistenceHelpers.ToRecord(existing);
        context.ChangeTracker.Clear();
        return result;
    }

    /// <summary>
    /// Cursor-paginated message replay scoped strictly to tenant and an authorized participant in the conversation.
    /// </summary>
    public async ValueTask<IReadOnlyList<MessageRecord>> ReplayMessagesAsync(
        TenantId tenantId,
        ConversationId conversationId,
        ParticipantKind participantKind,
        PrincipalId? participantPrincipalId,
        AgentId? participantAgentId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit), "Replay limit must be between 1 and 500.");

        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversationId);
        var participantKey = MessagingPersistenceHelpers.BuildParticipantKey(
            participantKind,
            participantPrincipalId,
            participantAgentId);

        var isAuthorizedMember = await context.Set<MessagingParticipantEntity>()
            .AsNoTracking()
            .AnyAsync(
                p => p.TenantId == tenant
                    && p.ConversationId == conv
                    && p.ParticipantKey == participantKey
                    && p.Status == nameof(ParticipantStatus.Active),
                cancellationToken)
            .ConfigureAwait(false);

        if (!isAuthorizedMember)
            throw new MessagingDomainException("unauthorized", "Actor is not an active participant of this conversation.", 403);

        var rows = await context.Set<MessagingMessageEntity>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenant && m.ConversationId == conv && m.Sequence > afterSequence)
            .OrderBy(m => m.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(MessagingPersistenceHelpers.ToRecord).ToArray();
    }
}
