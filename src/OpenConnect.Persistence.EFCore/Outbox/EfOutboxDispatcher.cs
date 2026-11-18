using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Providers;

namespace OpenConnect.Persistence.EFCore.Outbox;

/// <summary>Dispatches durable claims with bounded retries and lease-fenced completion.</summary>
public sealed class EfOutboxDispatcher
{
    /// <summary>Claims due entries and independently persists delivery, backoff, or quarantine.</summary>
    public Task<int> ExecuteBatchAsync<TContext>(TContext db, ICommittedOutboxPublisher publisher,
        string leaseOwner, TimeSpan leaseDuration, int batchSize = 50, int maxAttempts = 5,
        CancellationToken cancellationToken = default) where TContext : DbContext =>
        ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.Auto,
            new OutboxClaimRequest(batchSize, DateTime.UtcNow, leaseOwner, leaseDuration),
            publisher, maxAttempts, cancellationToken);

    internal async Task<int> ExecuteBatchAsync<TContext>(TContext db, OpenConnectOutboxClaimStrategy strategy, OutboxClaimRequest request, ICommittedOutboxPublisher publisher, int maxAttempts, CancellationToken ct)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publisher);
        if (maxAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var dialect = EfOutboxClaimDialectSelector.Select(db, strategy);
        var claims = await dialect.ClaimBatchAsync(db, request, ct).ConfigureAwait(false);
        var delivered = 0;
        foreach (var claim in claims)
        {
            try
            {
                var row = await OutboxClaimOperations.LoadCurrentClaimAsync(db, claim, ct).ConfigureAwait(false);
                if (row is null)
                    continue;
                await publisher.PublishAsync(row.Subject, row.EventId, row.SanitizedPayload, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (await dialect.CompleteClaimAsync(db, claim, ct).ConfigureAwait(false))
                    delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A broker may wrap cancellation in another exception. Ownership
                // loss must never consume a retry or quarantine an innocent row.
                ct.ThrowIfCancellationRequested();
                var row = await OutboxClaimOperations.LoadCurrentClaimAsync(db, claim, ct).ConfigureAwait(false);
                if (row is null)
                    continue;
                row.Attempts++;
                row.NextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 8))));
                if (row.Attempts >= maxAttempts)
                    row.Status = "quarantined";
                row.LeaseOwner = null;
                row.LeaseExpiresUtc = null;
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
