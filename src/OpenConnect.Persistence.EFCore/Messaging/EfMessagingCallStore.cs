using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Persistence.EFCore.Messaging;

public sealed class EfMessagingCallStore<TContext>(TContext context)
    : IMessagingCallStore
    where TContext : DbContext
{
    public async ValueTask<MessagingCallRecord> CreateCallAsync(
        MessagingCallRecord call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var applicationId = MessagingPersistenceHelpers.RequireApplicationId(call.ApplicationId);
        var tenant = MessagingPersistenceHelpers.Scope(call.TenantId);
        var conv = MessagingPersistenceHelpers.Scope(call.ConversationId);

        if (call.AllowedHumanRecipients.Count == 0)
            throw new MessagingDomainException("invalid_request", "A call must include at least one allowed human recipient.", 400);

        var conversation = await context.Set<MessagingConversationEntity>()
            .AsNoTracking()
            .Include(c => c.Participants)
            .SingleOrDefaultAsync(
                c => c.TenantId == tenant && c.ApplicationId == applicationId && c.ConversationId == conv,
                cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
            throw new MessagingDomainException("not_found", "Conversation not found.", 404);

        if (!string.Equals(conversation.State, nameof(ConversationState.Active), StringComparison.Ordinal))
            throw new MessagingDomainException("conflict", "Cannot start a call in a non-active conversation.", 409);

        var hasActiveHuman = conversation.Participants.Any(p =>
            string.Equals(p.Kind, nameof(ParticipantKind.Human), StringComparison.Ordinal)
            && string.Equals(p.Status, nameof(ParticipantStatus.Active), StringComparison.Ordinal));
        if (!hasActiveHuman)
            throw new MessagingDomainException("conflict", "Cannot start a call without an active human participant.", 409);

        var entity = new MessagingCallEntity
        {
            TenantId = tenant,
            ApplicationId = applicationId,
            CallId = MessagingPersistenceHelpers.Scope(call.CallId),
            ConversationId = conv,
            CallerKind = call.CallerKind.ToString(),
            CallerPrincipalId = call.CallerPrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(call.CallerPrincipalId.Value) : null,
            CallerAgentId = call.CallerAgentId.HasValue ? MessagingPersistenceHelpers.Scope(call.CallerAgentId.Value) : null,
            AllowedHumanRecipientsJson = MessagingPersistenceHelpers.SerializePrincipalIds(call.AllowedHumanRecipients),
            State = call.State.ToString(),
            MediaSecurityMode = call.MediaSecurityMode.ToString(),
            CreatedAtUtc = MessagingPersistenceHelpers.Utc(call.CreatedAt),
            ExpiresAtUtc = MessagingPersistenceHelpers.Utc(call.ExpiresAt),
            AcceptedDeviceId = call.AcceptedDeviceId.HasValue ? MessagingPersistenceHelpers.Scope(call.AcceptedDeviceId.Value) : null,
            AcceptedPrincipalId = call.AcceptedPrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(call.AcceptedPrincipalId.Value) : null,
            VoiceSessionId = call.VoiceSessionId.HasValue ? MessagingPersistenceHelpers.Scope(call.VoiceSessionId.Value) : null,
            AdmissionTokenHash = call.AdmissionTokenHash,
            EndedAtUtc = MessagingPersistenceHelpers.Utc(call.EndedAt),
            EndReason = call.EndReason,
            Version = 1
        };

        context.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Call already exists.", 409, ex);
        }

        context.ChangeTracker.Clear();
        return ToRecord(entity);
    }

    public async ValueTask<MessagingCallRecord?> GetCallAsync(
        TenantId tenantId,
        CallId callId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var id = MessagingPersistenceHelpers.Scope(callId);

        var entity = await context.Set<MessagingCallEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.TenantId == tenant && c.CallId == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async ValueTask<IReadOnlyList<MessagingCallRecord>> GetActiveCallsForConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversationId);

        var rows = await context.Set<MessagingCallEntity>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenant
                && c.ConversationId == conv
                && MessagingPersistenceHelpers.ActiveCallStates.Contains(c.State))
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToRecord).ToArray();
    }

    public async ValueTask<bool> TryTransitionCallAsync(
        MessagingCallRecord updated,
        CallState expectedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var tenant = MessagingPersistenceHelpers.Scope(updated.TenantId);
        var id = MessagingPersistenceHelpers.Scope(updated.CallId);
        var expected = expectedState.ToString();
        var nextState = updated.State.ToString();
        var acceptedDeviceId = updated.AcceptedDeviceId.HasValue ? MessagingPersistenceHelpers.Scope(updated.AcceptedDeviceId.Value) : null;
        var acceptedPrincipalId = updated.AcceptedPrincipalId.HasValue ? MessagingPersistenceHelpers.Scope(updated.AcceptedPrincipalId.Value) : null;
        var voiceSessionId = updated.VoiceSessionId.HasValue ? MessagingPersistenceHelpers.Scope(updated.VoiceSessionId.Value) : null;
        var endedAtUtc = MessagingPersistenceHelpers.Utc(updated.EndedAt);

        var affected = await context.Set<MessagingCallEntity>()
            .Where(c => c.TenantId == tenant && c.CallId == id && c.State == expected)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.State, nextState)
                .SetProperty(c => c.AcceptedDeviceId, acceptedDeviceId)
                .SetProperty(c => c.AcceptedPrincipalId, acceptedPrincipalId)
                .SetProperty(c => c.VoiceSessionId, voiceSessionId)
                .SetProperty(c => c.AdmissionTokenHash, updated.AdmissionTokenHash)
                .SetProperty(c => c.EndedAtUtc, endedAtUtc)
                .SetProperty(c => c.EndReason, updated.EndReason)
                .SetProperty(c => c.Version, c => c.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    private static MessagingCallRecord ToRecord(MessagingCallEntity c) =>
        new(
            new TenantId(Ulid.Parse(c.TenantId)),
            c.ApplicationId,
            new CallId(Ulid.Parse(c.CallId)),
            new ConversationId(Ulid.Parse(c.ConversationId)),
            Enum.Parse<ParticipantKind>(c.CallerKind),
            c.CallerPrincipalId is null ? null : new PrincipalId(Ulid.Parse(c.CallerPrincipalId)),
            c.CallerAgentId is null ? null : new AgentId(c.CallerAgentId),
            MessagingPersistenceHelpers.DeserializePrincipalIds(c.AllowedHumanRecipientsJson),
            Enum.Parse<CallState>(c.State),
            Enum.Parse<CallMediaSecurityMode>(c.MediaSecurityMode),
            MessagingPersistenceHelpers.FromUtc(c.CreatedAtUtc),
            MessagingPersistenceHelpers.FromUtc(c.ExpiresAtUtc),
            c.AcceptedDeviceId is null ? null : new DeviceId(Ulid.Parse(c.AcceptedDeviceId)),
            c.AcceptedPrincipalId is null ? null : new PrincipalId(Ulid.Parse(c.AcceptedPrincipalId)),
            c.VoiceSessionId is null ? null : new SessionId(Ulid.Parse(c.VoiceSessionId)),
            c.AdmissionTokenHash,
            MessagingPersistenceHelpers.FromUtc(c.EndedAtUtc),
            c.EndReason);
}
