using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfEventJournal<TContext>(TContext context) : IEventJournal where TContext : DbContext
{
    private const int MaxSequenceAttempts = 64;

    public async Task<long> AppendAsync(EventRecord @event, CancellationToken cancellationToken = default)
    {
        var session = UlidGuidMapper.ToGuid(@event.SessionId.Value);
        var owner = await context.Set<SessionEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session, cancellationToken).ConfigureAwait(false);
        if (owner is null) throw new KeyNotFoundException("session_not_found");

        var set = context.Set<EventEntity>();
        // OC2-D-DATA-017: the (TenantScope, SessionId, Sequence) unique index is the fence. Concurrent admitted
        // appends may read the same max; the loser's insert violates the index, is detected provider-neutrally by
        // re-querying the contested sequence, and retries with the next value. Inside an ambient transaction a
        // failed statement may poison the transaction (e.g. PostgreSQL), so the conflict is surfaced instead.
        for (var attempt = 0; ; attempt++)
        {
            var max = await set.AsNoTracking().Where(x => x.SessionId == session).Select(x => (long?)x.Sequence).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            var sequence = max + 1;
            // EF-02H session-anchored tenant stamping; caller is responsible for payload sanitization.
            var row = new EventEntity { EventId = Guid.NewGuid(), TenantScope = owner.TenantScope, SessionId = session, Sequence = sequence, EventType = @event.Type, SanitizedPayload = @event.Data, OccurredUtc = DateTime.UtcNow, CorrelationId = null };
            set.Add(row);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return sequence;
            }
            catch (DbUpdateException) when (context.Database.CurrentTransaction is null && attempt < MaxSequenceAttempts)
            {
                context.Entry(row).State = EntityState.Detached;
                var taken = await set.AsNoTracking().AnyAsync(x => x.TenantScope == owner.TenantScope && x.SessionId == session && x.Sequence == sequence, cancellationToken).ConfigureAwait(false);
                if (!taken) throw;
            }
            catch (DbUpdateException) when (context.Database.CurrentTransaction is not null)
            {
                context.Entry(row).State = EntityState.Detached;
                throw new InvalidOperationException($"{ErrorCodes.EfConcurrencyConflict}: concurrent event append inside an ambient transaction; retry the whole unit of work.");
            }
        }
    }

    public async IAsyncEnumerable<EventRecord> ReplayAsync(SessionId sessionId, long afterSequence = 0, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = UlidGuidMapper.ToGuid(sessionId.Value);
        var owner = await context.Set<SessionEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session, cancellationToken).ConfigureAwait(false);
        if (owner is null) yield break;

        var rows = await context.Set<EventEntity>().AsNoTracking().Where(x => x.SessionId == session && x.TenantScope == owner.TenantScope && x.Sequence > afterSequence).OrderBy(x => x.Sequence).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); yield return new(sessionId, row.Sequence, row.EventType, row.SanitizedPayload); }
    }
}
