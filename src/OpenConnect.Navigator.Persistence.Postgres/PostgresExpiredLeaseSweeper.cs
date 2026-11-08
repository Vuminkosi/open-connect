using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.Postgres;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>Explicitly reclaims long-expired operational lease rows.</summary>
/// <remarks>
/// Hosts own scheduling and must invoke this operation explicitly; it is never run during startup.
/// The expiry-only predicate is row-local and does not apply tenant or authorization filtering.
/// Receipt-bearing task rows are intentionally retained: approvals reference them and receipts are
/// audit/idempotency records. Deleting a lease resets its fencing sequence on the next acquisition;
/// this is considered safe only when retention (default 7 days) greatly exceeds the maximum lease
/// duration, which is bounded by operational job deadlines (typically minutes). Hosts coordinating
/// multiple sweepers must serialize concurrent sweeps.
/// </remarks>
/// <param name="dbContext">The host-configured persistence context.</param>
public sealed class PostgresExpiredLeaseSweeper(OpenConnectDbContext dbContext)
{
    /// <summary>Deletes lease rows expired earlier than the supplied retention cutoff.</summary>
    /// <param name="retention">The age an expiry must exceed before its row is reclaimed; defaults to seven days at the host.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    /// <returns>The number of lease rows deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Retention is zero or negative.</exception>
    public Task<int> SweepAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "Retention must be greater than zero.");
        }

        var cutoff = DateTimeOffset.UtcNow - retention;
        return dbContext.Leases
            .Where(lease => lease.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
