using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Core.Messaging;

/// <summary>
/// Enforces immutable conversation security profiles, the mandatory human-inclusive invariant,
/// monotonic message receipts, and idempotent message commits with payload-mismatch conflict detection.
/// </summary>
public sealed class ConversationCoordinator : IMessagingCoordinator
{
    private readonly IMessagingConversationStore _store;
    private readonly IMessagingAuthorizer _authorizer;
    private readonly IMessagingRecipientResolver _recipientResolver;
    private readonly TimeProvider _timeProvider;

    public ConversationCoordinator(
        IMessagingConversationStore store,
        IMessagingAuthorizer authorizer,
        IMessagingRecipientResolver recipientResolver,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _recipientResolver = recipientResolver ?? throw new ArgumentNullException(nameof(recipientResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ConversationRecord> CreateConversationAsync(
        MessagingActor creator,
        ConversationSecurityProfile securityProfile,
        IReadOnlyList<MessagingActor> initialParticipants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentNullException.ThrowIfNull(initialParticipants);
        ValidateActorIdentity(creator);
        if (!Enum.IsDefined(securityProfile))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Security profile is invalid.", 400);

        var auth = await _authorizer.AuthorizeAsync(creator, MessagingPermission.Initiate, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Conversation creation denied.", 403);

        var combined = new List<MessagingActor>(initialParticipants.Count + 1) { creator };
        foreach (var candidate in initialParticipants)
        {
            if (candidate is null)
                throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Participant entry cannot be null.", 400);
            if (!IsSameParticipant(creator, candidate))
                combined.Add(candidate);
        }

        var now = _timeProvider.GetUtcNow();
        var conversationId = new ConversationId(Ulid.NewUlid());
        var resolvedParticipants = new List<ConversationParticipant>(combined.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in combined)
        {
            ValidateActorIdentity(candidate);
            if (candidate.TenantId != creator.TenantId ||
                !string.Equals(candidate.ApplicationId, creator.ApplicationId, StringComparison.Ordinal))
            {
                throw new MessagingDomainException(ErrorCodes.Unauthorized, "Cross-tenant or cross-application participants are prohibited.", 403);
            }

            var resolved = await ResolveActorAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
                throw new MessagingDomainException(ErrorCodes.Unauthorized, "Participant could not be resolved or is not authorized.", 403);

            var key = GetParticipantKey(resolved);
            if (!seenKeys.Add(key))
                continue;

            var role = IsSameParticipant(resolved, creator)
                ? (resolved.Kind == ParticipantKind.Human ? ParticipantRole.Owner : ParticipantRole.OperationalAgent)
                : (resolved.Kind == ParticipantKind.Human ? ParticipantRole.Member : ParticipantRole.OperationalAgent);

            resolvedParticipants.Add(new ConversationParticipant(
                conversationId,
                resolved.Kind,
                resolved.PrincipalId,
                resolved.AgentId,
                role,
                ParticipantStatus.Active,
                JoinVersion: 1,
                LeaveVersion: null,
                JoinedAt: now));
        }

        EnforceHumanInclusiveAndProfileInvariants(securityProfile, resolvedParticipants);

        var record = new ConversationRecord(
            creator.TenantId,
            creator.ApplicationId,
            conversationId,
            securityProfile,
            ConversationState.Active,
            MembershipVersion: 1,
            CryptoEpoch: 1,
            NextSequence: 1,
            CreatedAt: now,
            Participants: resolvedParticipants);

        return await _store.CreateConversationAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConversationRecord> AddParticipantAsync(
        MessagingActor actor,
        ConversationId conversationId,
        MessagingActor participantToAdd,
        ParticipantRole role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(participantToAdd);
        ValidateActorIdentity(actor);
        ValidateActorIdentity(participantToAdd);

        var conversation = await _store.GetConversationAsync(actor.TenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != actor.TenantId ||
            !string.Equals(conversation.ApplicationId, actor.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Conversation was not found.", 404);
        }

        if (conversation.State != ConversationState.Active)
            throw new MessagingDomainException(ErrorCodes.ConversationClosed, "Conversation is closed.", 409);

        if (!IsActiveParticipant(conversation, actor))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Actor is not an active conversation participant.", 403);

        var auth = await _authorizer.AuthorizeAsync(actor, MessagingPermission.Invite, conversationId, participantToAdd, cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Participant addition denied.", 403);

        if (participantToAdd.TenantId != conversation.TenantId ||
            !string.Equals(participantToAdd.ApplicationId, conversation.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Cross-tenant participant addition is prohibited.", 403);
        }

        if (conversation.SecurityProfile == ConversationSecurityProfile.PrivateE2EE &&
            participantToAdd.Kind == ParticipantKind.Agent)
        {
            throw new MessagingDomainException(
                ErrorCodes.PrivateE2EEAgentForbidden,
                "Cannot add an agent to a PrivateE2EE conversation; security profile is immutable.",
                409);
        }

        var resolved = await ResolveActorAsync(participantToAdd, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Target participant could not be resolved.", 403);

        if (IsActiveParticipant(conversation, resolved))
            throw new MessagingDomainException(ErrorCodes.Conflict, "Participant is already active in this conversation.", 409);

        var nextVersion = conversation.MembershipVersion + 1;
        var nextEpoch = conversation.CryptoEpoch + 1;
        var now = _timeProvider.GetUtcNow();

        var updatedParticipants = new List<ConversationParticipant>(conversation.Participants.Count + 1);
        updatedParticipants.AddRange(conversation.Participants);
        updatedParticipants.Add(new ConversationParticipant(
            conversationId,
            resolved.Kind,
            resolved.PrincipalId,
            resolved.AgentId,
            role,
            ParticipantStatus.Active,
            JoinVersion: nextVersion,
            LeaveVersion: null,
            JoinedAt: now));

        var updated = conversation with
        {
            MembershipVersion = nextVersion,
            CryptoEpoch = nextEpoch,
            Participants = updatedParticipants
        };

        return await _store.UpdateConversationAsync(updated, conversation.MembershipVersion, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ParticipantRemovalResult> RemoveParticipantAsync(
        MessagingActor actor,
        ConversationId conversationId,
        ParticipantKind targetKind,
        PrincipalId? targetPrincipalId,
        AgentId? targetAgentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateActorIdentity(actor);

        var conversation = await _store.GetConversationAsync(actor.TenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != actor.TenantId ||
            !string.Equals(conversation.ApplicationId, actor.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Conversation was not found.", 404);
        }

        if (conversation.State != ConversationState.Active)
            throw new MessagingDomainException(ErrorCodes.ConversationClosed, "Conversation is already closed.", 409);

        if (!IsActiveParticipant(conversation, actor))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Actor is not an active conversation participant.", 403);

        var isSelfLeave = actor.Kind == targetKind &&
            ((targetKind == ParticipantKind.Human && actor.PrincipalId == targetPrincipalId) ||
             (targetKind == ParticipantKind.Agent && actor.AgentId == targetAgentId));

        if (!isSelfLeave)
        {
            var auth = await _authorizer.AuthorizeAsync(actor, MessagingPermission.Invite, conversationId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!auth.Allowed)
                throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Participant removal denied.", 403);
        }

        var nextVersion = conversation.MembershipVersion + 1;
        var nextEpoch = conversation.CryptoEpoch + 1;
        var foundActive = false;
        var updatedParticipants = new List<ConversationParticipant>(conversation.Participants.Count);

        foreach (var p in conversation.Participants)
        {
            var matches = p.Status == ParticipantStatus.Active && p.Kind == targetKind &&
                ((targetKind == ParticipantKind.Human && p.PrincipalId == targetPrincipalId) ||
                 (targetKind == ParticipantKind.Agent && p.AgentId == targetAgentId));
            if (matches)
            {
                foundActive = true;
                updatedParticipants.Add(p with
                {
                    Status = isSelfLeave ? ParticipantStatus.Left : ParticipantStatus.Removed,
                    LeaveVersion = nextVersion
                });
            }
            else
            {
                updatedParticipants.Add(p);
            }
        }

        if (!foundActive)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Target participant is not active in this conversation.", 404);

        var remainingHumans = updatedParticipants.Count(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active);
        var closeConversation = remainingHumans == 0;

        var updated = conversation with
        {
            State = closeConversation ? ConversationState.Closed : conversation.State,
            MembershipVersion = nextVersion,
            CryptoEpoch = nextEpoch,
            Participants = updatedParticipants
        };

        var persisted = await _store.UpdateConversationAsync(updated, conversation.MembershipVersion, cancellationToken).ConfigureAwait(false);
        return new ParticipantRemovalResult(persisted, closeConversation, closeConversation);
    }

    public async ValueTask<MessageCommitResult> CommitMessageAsync(
        MessagingActor sender,
        ConversationId conversationId,
        EncryptedMessageEnvelope envelope,
        string clientOperationId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateActorIdentity(sender);

        if (string.IsNullOrWhiteSpace(clientOperationId) || clientOperationId.Length > 128)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "A valid clientOperationId is required.", 400);
        if (string.IsNullOrWhiteSpace(envelope.Protocol) ||
            string.IsNullOrWhiteSpace(envelope.CiphertextBase64) ||
            string.IsNullOrWhiteSpace(envelope.NonceBase64) ||
            string.IsNullOrWhiteSpace(envelope.AuthTagBase64))
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Encrypted message envelope is incomplete.", 400);
        }

        var conversation = await _store.GetConversationAsync(sender.TenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != sender.TenantId ||
            !string.Equals(conversation.ApplicationId, sender.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Conversation was not found.", 404);
        }

        if (conversation.State != ConversationState.Active)
            throw new MessagingDomainException(ErrorCodes.ConversationClosed, "Conversation is closed to further sends.", 409);

        if (conversation.Participants.All(p => p.Kind != ParticipantKind.Human || p.Status != ParticipantStatus.Active))
            throw new MessagingDomainException(ErrorCodes.HumanParticipantRequired, "Conversation has no active human participant.", 409);

        if (!IsActiveParticipant(conversation, sender))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Sender is not an active participant.", 403);

        var auth = await _authorizer.AuthorizeAsync(sender, MessagingPermission.Send, conversationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Message send denied.", 403);

        var digest = ComputeEnvelopeDigestSha256(envelope);
        var existing = await _store.GetMessageByClientOperationIdAsync(sender.TenantId, conversationId, clientOperationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingBytes = Encoding.UTF8.GetBytes(existing.PayloadDigestSha256);
            var incomingBytes = Encoding.UTF8.GetBytes(digest);
            if (existingBytes.Length == incomingBytes.Length &&
                CryptographicOperations.FixedTimeEquals(existingBytes, incomingBytes))
            {
                return new MessageCommitResult(existing, WasDeduplicated: true);
            }

            throw new MessagingDomainException(
                ErrorCodes.Conflict,
                "ClientOperationId was already used with a different message payload.",
                409);
        }

        if (envelope.CryptoEpoch != conversation.CryptoEpoch)
        {
            throw new MessagingDomainException(
                ErrorCodes.CryptoEpochMismatch,
                $"Envelope epoch {envelope.CryptoEpoch} does not match conversation epoch {conversation.CryptoEpoch}.",
                409);
        }

        var message = new MessageRecord(
            sender.TenantId,
            conversationId,
            new MessageId(Ulid.NewUlid()),
            conversation.NextSequence,
            sender.Kind,
            sender.PrincipalId,
            sender.AgentId,
            sender.DeviceId,
            envelope,
            clientOperationId,
            digest,
            _timeProvider.GetUtcNow(),
            correlationId);

        var committed = await _store.CommitMessageAsync(message, cancellationToken).ConfigureAwait(false);
        return new MessageCommitResult(committed, WasDeduplicated: false);
    }

    public async ValueTask<MessageReceiptRecord> RecordReceiptAsync(
        MessagingActor actor,
        ConversationId conversationId,
        MessageId messageId,
        MessageReceiptKind kind,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateActorIdentity(actor);
        if (!Enum.IsDefined(kind))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Receipt kind is invalid.", 400);
        if (actor.Kind != ParticipantKind.Human || actor.PrincipalId is null || actor.DeviceId is null || actor.DeviceId.Value.Value == default)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Authenticated human device identity is required for receipts.", 403);

        var conversation = await _store.GetConversationAsync(actor.TenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != actor.TenantId)
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Conversation was not found.", 404);

        if (!IsActiveParticipant(conversation, actor))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Actor is not an active participant.", 403);

        var auth = await _authorizer.AuthorizeAsync(actor, MessagingPermission.Receive, conversationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Receipt denied.", 403);

        var message = await _store.GetMessageAsync(actor.TenantId, conversationId, messageId, cancellationToken).ConfigureAwait(false);
        if (message is null)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Message was not found in this conversation.", 404);

        var existing = await _store.GetReceiptAsync(actor.TenantId, messageId, actor.DeviceId.Value, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !IsAllowedReceiptTransition(existing.Kind, kind))
            return existing;

        var next = new MessageReceiptRecord(
            actor.TenantId,
            conversationId,
            messageId,
            actor.PrincipalId.Value,
            actor.DeviceId.Value,
            kind,
            message.Sequence,
            _timeProvider.GetUtcNow(),
            kind == MessageReceiptKind.Failed ? failureReason : null);

        return await _store.UpsertReceiptAsync(next, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsAllowedReceiptTransition(MessageReceiptKind current, MessageReceiptKind next)
    {
        if (current == next)
            return false;

        if (next == MessageReceiptKind.Failed)
            return current is MessageReceiptKind.Queued or MessageReceiptKind.Committed;

        if (current == MessageReceiptKind.Failed)
            return next is MessageReceiptKind.Committed or MessageReceiptKind.Delivered or MessageReceiptKind.Read;

        return GetReceiptRank(next) > GetReceiptRank(current);
    }

    public static string ComputeEnvelopeDigestSha256(EncryptedMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var canonical = string.Join(
            '|',
            envelope.Protocol,
            envelope.CryptoEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.CiphertextBase64,
            envelope.NonceBase64,
            envelope.AuthTagBase64,
            envelope.AssociatedDataBase64 ?? string.Empty,
            envelope.SenderKeyId ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int GetReceiptRank(MessageReceiptKind kind) => kind switch
    {
        MessageReceiptKind.Queued => 0,
        MessageReceiptKind.Committed => 1,
        MessageReceiptKind.Delivered => 2,
        MessageReceiptKind.Read => 3,
        _ => -1
    };

    private async ValueTask<MessagingActor?> ResolveActorAsync(MessagingActor candidate, CancellationToken cancellationToken)
    {
        if (candidate.Kind == ParticipantKind.Human && candidate.PrincipalId is not null)
            return await _recipientResolver.ResolveHumanAsync(candidate.TenantId, candidate.ApplicationId, candidate.PrincipalId.Value, cancellationToken).ConfigureAwait(false);
        if (candidate.Kind == ParticipantKind.Agent && candidate.AgentId is not null)
            return await _recipientResolver.ResolveAgentAsync(candidate.TenantId, candidate.ApplicationId, candidate.AgentId.Value, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static void EnforceHumanInclusiveAndProfileInvariants(
        ConversationSecurityProfile profile,
        IReadOnlyList<ConversationParticipant> participants)
    {
        var activeHumans = participants.Count(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active);
        if (activeHumans < 1)
        {
            throw new MessagingDomainException(
                ErrorCodes.HumanParticipantRequired,
                "Every conversation must include at least one authorized human participant.",
                400);
        }

        if (profile == ConversationSecurityProfile.PrivateE2EE &&
            participants.Any(p => p.Kind == ParticipantKind.Agent))
        {
            throw new MessagingDomainException(
                ErrorCodes.PrivateE2EEAgentForbidden,
                "PrivateE2EE conversations cannot include agent participants.",
                400);
        }
    }

    private static void ValidateActorIdentity(MessagingActor actor)
    {
        if (actor.TenantId.Value == default || string.IsNullOrWhiteSpace(actor.ApplicationId))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Tenant and application identity are required.", 401);
        if (actor.Kind == ParticipantKind.Human && (actor.PrincipalId is null || actor.PrincipalId.Value.Value == default))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Human principal identity is required.", 401);
        if (actor.Kind == ParticipantKind.Agent && (actor.AgentId is null || string.IsNullOrWhiteSpace(actor.AgentId.Value.Value)))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Agent identity is required.", 401);
    }

    private static bool IsActiveParticipant(ConversationRecord conversation, MessagingActor actor)
    {
        foreach (var p in conversation.Participants)
        {
            if (p.Status != ParticipantStatus.Active || p.Kind != actor.Kind)
                continue;
            if (actor.Kind == ParticipantKind.Human && p.PrincipalId == actor.PrincipalId)
                return true;
            if (actor.Kind == ParticipantKind.Agent && p.AgentId == actor.AgentId)
                return true;
        }
        return false;
    }

    private static bool IsSameParticipant(MessagingActor left, MessagingActor right) =>
        left.Kind == right.Kind &&
        ((left.Kind == ParticipantKind.Human && left.PrincipalId == right.PrincipalId) ||
         (left.Kind == ParticipantKind.Agent && left.AgentId == right.AgentId));

    private static string GetParticipantKey(MessagingActor actor) =>
        actor.Kind == ParticipantKind.Human
            ? $"H:{actor.PrincipalId!.Value.Value}"
            : $"A:{actor.AgentId!.Value.Value}";
}
