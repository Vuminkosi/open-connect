using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfEventJournal<TContext>(TContext context) : IEventJournal where TContext : DbContext
{
    public async Task<long> AppendAsync(EventRecord @event, CancellationToken cancellationToken = default)
    {
        var session = UlidGuidMapper.ToGuid(@event.SessionId.Value);
        var owner = await context.Set<SessionEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session, cancellationToken).ConfigureAwait(false);
        if (owner is null) throw new KeyNotFoundException("session_not_found");

        var set = context.Set<EventEntity>();
        var max = await set.Where(x => x.SessionId == session).Select(x => (long?)x.Sequence).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var sequence = max + 1; // EF-02D master-approved interim mapping: single writer per session is assumed.
        // EF-02H session-anchored tenant stamping; caller is responsible for payload sanitization.
        set.Add(new EventEntity { EventId = Guid.NewGuid(), TenantScope = owner.TenantScope, SessionId = session, Sequence = sequence, EventType = @event.Type, SanitizedPayload = @event.Data, OccurredUtc = DateTime.UtcNow, CorrelationId = null });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sequence;
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
