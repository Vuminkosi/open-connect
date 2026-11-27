using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Persistence.EFCore.Messaging;

public sealed class EfFollowUpStore<TContext>(TContext context)
    : IFollowUpStore
    where TContext : DbContext
{
    public async ValueTask<FollowUpRecord> CreateFollowUpAsync(
        FollowUpRecord followUp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(followUp);
        var applicationId = MessagingPersistenceHelpers.RequireApplicationId(followUp.ApplicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(followUp.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(followUp.CausalEventId);

        var entity = new MessagingFollowUpEntity
        {
            TenantId = MessagingPersistenceHelpers.Scope(followUp.TenantId),
            ApplicationId = applicationId,
            FollowUpId = MessagingPersistenceHelpers.Scope(followUp.FollowUpId),
            ConversationId = MessagingPersistenceHelpers.Scope(followUp.ConversationId),
            TaskId = followUp.TaskId.HasValue ? MessagingPersistenceHelpers.Scope(followUp.TaskId.Value) : null,
            AgentId = MessagingPersistenceHelpers.Scope(followUp.AgentId),
            RecipientPrincipalId = MessagingPersistenceHelpers.Scope(followUp.RecipientPrincipalId),
            DueAtUtc = MessagingPersistenceHelpers.Utc(followUp.DueAtUtc),
            Attempt = followUp.Attempt,
            MaxAttempts = followUp.MaxAttempts,
            State = followUp.State.ToString(),
            IdempotencyKey = followUp.IdempotencyKey,
            CausalEventId = followUp.CausalEventId,
            LeaseOwner = followUp.LeaseOwner,
            LeaseFence = followUp.LeaseFence,
            LeaseExpiresAtUtc = MessagingPersistenceHelpers.Utc(followUp.LeaseExpiresAtUtc),
            QuietHoursStartUtc = MessagingPersistenceHelpers.FormatTimeOnly(followUp.QuietHoursStartUtc),
            QuietHoursEndUtc = MessagingPersistenceHelpers.FormatTimeOnly(followUp.QuietHoursEndUtc),
            TimeZoneId = followUp.TimeZoneId,
            LastDispatchedAtUtc = MessagingPersistenceHelpers.Utc(followUp.LastDispatchedAtUtc),
            CompletedAtUtc = MessagingPersistenceHelpers.Utc(followUp.CompletedAtUtc)
        };

        context.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Follow-up already exists or violates idempotency uniqueness.", 409, ex);
        }

        context.ChangeTracker.Clear();
        return ToFollowUpRecord(entity);
    }

    public async ValueTask<FollowUpRecord?> GetFollowUpAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var id = MessagingPersistenceHelpers.Scope(followUpId);

        var entity = await context.Set<MessagingFollowUpEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(f => f.TenantId == tenant && f.FollowUpId == id, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToFollowUpRecord(entity);
    }

    public async ValueTask<IReadOnlyList<FollowUpRecord>> ClaimDueFollowUpsAsync(
        DateTimeOffset utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        int maxBatchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        if (maxBatchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Batch size must be between 1 and 500.");

        var nowUtc = MessagingPersistenceHelpers.Utc(utcNow);
        var leaseExpiresAtUtc = MessagingPersistenceHelpers.Utc(utcNow.Add(leaseDuration));

        var candidates = await context.Set<MessagingFollowUpEntity>()
            .AsNoTracking()
            .Where(f =>
                (f.State == nameof(FollowUpState.Scheduled) && f.DueAtUtc <= nowUtc)
                || (f.State == nameof(FollowUpState.Claimed) && f.LeaseExpiresAtUtc <= nowUtc))
            .OrderBy(f => f.DueAtUtc)
            .ThenBy(f => f.FollowUpId)
            .Take(maxBatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimed = new List<FollowUpRecord>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var expectedState = candidate.State;
            var expectedFence = candidate.LeaseFence;
            var nextFence = expectedFence + 1;

            var affected = await context.Set<MessagingFollowUpEntity>()
                .Where(f => f.TenantId == candidate.TenantId
                    && f.ApplicationId == candidate.ApplicationId
                    && f.FollowUpId == candidate.FollowUpId
                    && f.State == expectedState
                    && f.LeaseFence == expectedFence)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(f => f.State, nameof(FollowUpState.Claimed))
                    .SetProperty(f => f.LeaseOwner, leaseOwner)
                    .SetProperty(f => f.LeaseFence, nextFence)
                    .SetProperty(f => f.LeaseExpiresAtUtc, leaseExpiresAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 1)
            {
                candidate.State = nameof(FollowUpState.Claimed);
                candidate.LeaseOwner = leaseOwner;
                candidate.LeaseFence = nextFence;
                candidate.LeaseExpiresAtUtc = leaseExpiresAtUtc;
                claimed.Add(ToFollowUpRecord(candidate));
            }
        }

        return claimed;
    }

    public async ValueTask<bool> TryTransitionFollowUpAsync(
        FollowUpRecord updated,
        FollowUpState expectedState,
        long expectedLeaseFence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var tenant = MessagingPersistenceHelpers.Scope(updated.TenantId);
        var id = MessagingPersistenceHelpers.Scope(updated.FollowUpId);
        var expected = expectedState.ToString();
        var nextState = updated.State.ToString();
        var dueAtUtc = MessagingPersistenceHelpers.Utc(updated.DueAtUtc);
        var leaseExpiresAtUtc = MessagingPersistenceHelpers.Utc(updated.LeaseExpiresAtUtc);
        var lastDispatchedAtUtc = MessagingPersistenceHelpers.Utc(updated.LastDispatchedAtUtc);
        var completedAtUtc = MessagingPersistenceHelpers.Utc(updated.CompletedAtUtc);

        var affected = await context.Set<MessagingFollowUpEntity>()
            .Where(f => f.TenantId == tenant
                && f.FollowUpId == id
                && f.State == expected
                && f.LeaseFence == expectedLeaseFence)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.State, nextState)
                .SetProperty(f => f.Attempt, updated.Attempt)
                .SetProperty(f => f.DueAtUtc, dueAtUtc)
                .SetProperty(f => f.LeaseOwner, updated.LeaseOwner)
                .SetProperty(f => f.LeaseFence, updated.LeaseFence)
                .SetProperty(f => f.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                .SetProperty(f => f.LastDispatchedAtUtc, lastDispatchedAtUtc)
                .SetProperty(f => f.CompletedAtUtc, completedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public async ValueTask<IReadOnlyList<FollowUpRecord>> GetActiveFollowUpsForConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        PrincipalId recipientPrincipalId,
        CancellationToken cancellationToken = default)
    {
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var conv = MessagingPersistenceHelpers.Scope(conversationId);
        var recipient = MessagingPersistenceHelpers.Scope(recipientPrincipalId);

        var rows = await context.Set<MessagingFollowUpEntity>()
            .AsNoTracking()
            .Where(f => f.TenantId == tenant
                && f.ConversationId == conv
                && f.RecipientPrincipalId == recipient
                && MessagingPersistenceHelpers.ActiveFollowUpStates.Contains(f.State))
            .OrderBy(f => f.DueAtUtc)
            .ThenBy(f => f.FollowUpId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToFollowUpRecord).ToArray();
    }

    public async ValueTask<ContinuationRecord> CreateContinuationAsync(
        ContinuationRecord continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuation.ExecutionIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuation.TriggeringEventId);

        var tenant = MessagingPersistenceHelpers.Scope(continuation.TenantId);
        var conv = MessagingPersistenceHelpers.Scope(continuation.ConversationId);

        var conversation = await context.Set<MessagingConversationEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.TenantId == tenant && c.ConversationId == conv, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
            throw new MessagingDomainException("not_found", "Conversation not found for continuation.", 404);

        var entity = new MessagingContinuationEntity
        {
            TenantId = tenant,
            ApplicationId = conversation.ApplicationId,
            ContinuationId = MessagingPersistenceHelpers.Scope(continuation.ContinuationId),
            ConversationId = conv,
            TriggeringEventId = continuation.TriggeringEventId,
            AgentId = MessagingPersistenceHelpers.Scope(continuation.AgentId),
            RecipientPrincipalId = MessagingPersistenceHelpers.Scope(continuation.RecipientPrincipalId),
            TaskId = continuation.TaskId.HasValue ? MessagingPersistenceHelpers.Scope(continuation.TaskId.Value) : null,
            SessionId = continuation.SessionId.HasValue ? MessagingPersistenceHelpers.Scope(continuation.SessionId.Value) : null,
            Status = continuation.Status.ToString(),
            ExecutionIdempotencyKey = continuation.ExecutionIdempotencyKey,
            LeaseOwner = continuation.LeaseOwner,
            LeaseFence = continuation.LeaseFence,
            CreatedAtUtc = MessagingPersistenceHelpers.Utc(continuation.CreatedAt == default ? DateTimeOffset.UtcNow : continuation.CreatedAt)
        };

        context.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            throw new MessagingDomainException("conflict", "Continuation execution idempotency key already exists.", 409, ex);
        }

        context.ChangeTracker.Clear();
        return ToContinuationRecord(entity);
    }

    /// <summary>
    /// Atomically resolves a human reply race against an active follow-up using CAS fencing and queues
    /// the resulting continuation inside a single DB transaction.
    /// </summary>
    public async ValueTask<FollowUpRaceResolution?> TryResolveHumanReplyRaceAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        FollowUpState expectedState,
        long expectedLeaseFence,
        ContinuationRecord continuation,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var tenant = MessagingPersistenceHelpers.Scope(tenantId);
        var id = MessagingPersistenceHelpers.Scope(followUpId);
        var expected = expectedState.ToString();
        var completedAtUtc = MessagingPersistenceHelpers.Utc(utcNow);

        await using var tx = await MessagingPersistenceHelpers.BeginIfNeededAsync(context, cancellationToken).ConfigureAwait(false);

        var affected = await context.Set<MessagingFollowUpEntity>()
            .Where(f => f.TenantId == tenant
                && f.FollowUpId == id
                && f.State == expected
                && f.LeaseFence == expectedLeaseFence)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.State, nameof(FollowUpState.ResponseReceived))
                .SetProperty(f => f.LeaseFence, expectedLeaseFence + 1)
                .SetProperty(f => f.CompletedAtUtc, completedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected != 1)
            return null;

        var savedContinuation = await CreateContinuationAsync(continuation, cancellationToken).ConfigureAwait(false);
        var updatedFollowUp = await GetFollowUpAsync(tenantId, followUpId, cancellationToken).ConfigureAwait(false);

        if (tx is not null)
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return updatedFollowUp is null
            ? null
            : new FollowUpRaceResolution(updatedFollowUp, SuppressedByHumanReply: true, QueuedContinuation: savedContinuation);
    }

    private static FollowUpRecord ToFollowUpRecord(MessagingFollowUpEntity f) =>
        new(
            new TenantId(Ulid.Parse(f.TenantId)),
            f.ApplicationId,
            new FollowUpId(Ulid.Parse(f.FollowUpId)),
            new ConversationId(Ulid.Parse(f.ConversationId)),
            f.TaskId is null ? null : new TaskId(Ulid.Parse(f.TaskId)),
            new AgentId(f.AgentId),
            new PrincipalId(Ulid.Parse(f.RecipientPrincipalId)),
            MessagingPersistenceHelpers.FromUtc(f.DueAtUtc),
            f.Attempt,
            f.MaxAttempts,
            Enum.Parse<FollowUpState>(f.State),
            f.IdempotencyKey,
            f.CausalEventId,
            f.LeaseOwner,
            f.LeaseFence,
            MessagingPersistenceHelpers.FromUtc(f.LeaseExpiresAtUtc),
            MessagingPersistenceHelpers.ParseTimeOnly(f.QuietHoursStartUtc),
            MessagingPersistenceHelpers.ParseTimeOnly(f.QuietHoursEndUtc),
            f.TimeZoneId,
            MessagingPersistenceHelpers.FromUtc(f.LastDispatchedAtUtc),
            MessagingPersistenceHelpers.FromUtc(f.CompletedAtUtc));

    private static ContinuationRecord ToContinuationRecord(MessagingContinuationEntity c) =>
        new(
            new TenantId(Ulid.Parse(c.TenantId)),
            new ContinuationId(Ulid.Parse(c.ContinuationId)),
            new ConversationId(Ulid.Parse(c.ConversationId)),
            c.TriggeringEventId,
            new AgentId(c.AgentId),
            new PrincipalId(Ulid.Parse(c.RecipientPrincipalId)),
            c.TaskId is null ? null : new TaskId(Ulid.Parse(c.TaskId)),
            c.SessionId is null ? null : new SessionId(Ulid.Parse(c.SessionId)),
            Enum.Parse<ContinuationStatus>(c.Status),
            c.ExecutionIdempotencyKey,
            c.LeaseOwner,
            c.LeaseFence,
            MessagingPersistenceHelpers.FromUtc(c.CreatedAtUtc));
}
