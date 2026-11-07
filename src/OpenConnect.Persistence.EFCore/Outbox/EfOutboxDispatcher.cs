using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Providers;

namespace OpenConnect.Persistence.EFCore.Outbox;

internal sealed class EfOutboxDispatcher
{
    public async Task<int> ExecuteBatchAsync<TContext>(TContext db, OpenConnectOutboxClaimStrategy strategy, OutboxClaimRequest request, ICommittedOutboxPublisher publisher, int maxAttempts, CancellationToken ct)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publisher);
        if (maxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var dialect = EfOutboxClaimDialectSelector.Select(db, strategy);
        var claims = await dialect.ClaimBatchAsync(db, request, ct).ConfigureAwait(false);
        var delivered = 0;
        foreach (var claim in claims)
        {
            try
            {
                var row = await db.Set<OutboxEntity>().FindAsync([Guid.Parse(claim.EntryId)], ct).ConfigureAwait(false);
                if (row is null) continue;
                await publisher.PublishAsync(row.Subject, row.EventId, row.SanitizedPayload, ct).ConfigureAwait(false);
                if (await dialect.CompleteClaimAsync(db, claim, ct).ConfigureAwait(false)) delivered++;
            }
            catch (Exception)
            {
                var row = await db.Set<OutboxEntity>().FindAsync([Guid.Parse(claim.EntryId)], ct).ConfigureAwait(false);
                if (row is null) continue;
                row.Attempts++;
                row.NextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 8))));
                if (row.Attempts >= maxAttempts) row.Status = "quarantined";
                try
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException)
                {
                    db.Entry(row).State = EntityState.Detached;
                }
            }
        }
        // Claims, publication completion, and retry updates use short SaveChanges transactions;
        // no database transaction is held across the external publish operation.
        return delivered;
    }
}
