using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Providers;

internal sealed record OutboxClaimRequest(int BatchSize, DateTime DueBeforeUtc, string LeaseOwner, TimeSpan LeaseDuration);

internal interface IEfOutboxClaimDialect
{
    string Name { get; }
    Task<IReadOnlyList<OutboxClaim>> ClaimBatchAsync(DbContext context, OutboxClaimRequest request, CancellationToken ct);
    Task<bool> CompleteClaimAsync(DbContext context, OutboxClaim claim, CancellationToken ct);
}

internal sealed class OptimisticConcurrencyClaimDialect : IEfOutboxClaimDialect
{
    public string Name => nameof(OpenConnectOutboxClaimStrategy.OptimisticConcurrency);
    public Task<IReadOnlyList<OutboxClaim>> ClaimBatchAsync(DbContext context, OutboxClaimRequest request, CancellationToken ct) => OutboxClaimOperations.ClaimBatchAsync(context, request, ct);
    public Task<bool> CompleteClaimAsync(DbContext context, OutboxClaim claim, CancellationToken ct) => OutboxClaimOperations.CompleteClaimAsync(context, claim, ct);
}

internal sealed class SqliteClaimDialect : IEfOutboxClaimDialect
{
    public string Name => "Sqlite";

    // SQLite uses the same compare-and-swap implementation. SQLite serializes writers;
    // keep writes short and use a single writer per row rather than provider-specific SQL.
    public Task<IReadOnlyList<OutboxClaim>> ClaimBatchAsync(DbContext context, OutboxClaimRequest request, CancellationToken ct) => OutboxClaimOperations.ClaimBatchAsync(context, request, ct);
    public Task<bool> CompleteClaimAsync(DbContext context, OutboxClaim claim, CancellationToken ct) => OutboxClaimOperations.CompleteClaimAsync(context, claim, ct);
}

internal static class OutboxClaimOperations
{
    internal static async Task<IReadOnlyList<OutboxClaim>> ClaimBatchAsync(DbContext context, OutboxClaimRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(request.BatchSize));
        if (string.IsNullOrWhiteSpace(request.LeaseOwner)) throw new ArgumentException("A lease owner is required.", nameof(request.LeaseOwner));
        if (request.LeaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(request.LeaseDuration));

        var now = DateTime.UtcNow;
        // The same owner may always redrive its rows (retry loop); a different owner may only take
        // rows never leased or whose lease expired (fenced handoff). The fencing token and
        // CompleteClaimAsync checks remain the second line of defense.
        var candidates = await context.Set<OutboxEntity>()
            .Where(x => x.Status == "pending" && x.NextAttemptUtc <= request.DueBeforeUtc && (x.LeaseOwner == null || x.LeaseOwner == request.LeaseOwner || x.LeaseExpiresUtc == null || x.LeaseExpiresUtc <= now))
            .OrderBy(x => x.NextAttemptUtc).ThenBy(x => x.Id).Take(request.BatchSize)
            .ToListAsync(ct).ConfigureAwait(false);
        var winners = new List<OutboxClaim>();
        foreach (var row in candidates)
        {
            row.LeaseOwner = request.LeaseOwner;
            row.LeaseExpiresUtc = now + request.LeaseDuration;
            row.FencingVersion++;
            try
            {
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                winners.Add(new OutboxClaim(row.Id.ToString(), row.TenantScope, row.FencingVersion, row.LeaseOwner, new DateTimeOffset(row.LeaseExpiresUtc.Value)));
            }
            catch (DbUpdateConcurrencyException)
            {
                context.Entry(row).State = EntityState.Detached;
            }
        }
        return winners;
    }

    internal static async Task<bool> CompleteClaimAsync(DbContext context, OutboxClaim claim, CancellationToken ct)
    {
        var id = Guid.Parse(claim.EntryId);
        var row = await context.Set<OutboxEntity>().FindAsync([id], ct).ConfigureAwait(false);
        if (row is null || row.Status != "pending" || row.LeaseOwner != claim.LeaseOwner || row.FencingVersion != claim.FencingVersion || row.LeaseExpiresUtc is null || row.LeaseExpiresUtc <= DateTime.UtcNow)
            return false;
        row.Status = "delivered";
        row.PublishedUtc = DateTime.UtcNow;
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.Entry(row).State = EntityState.Detached;
            return false;
        }
    }
}
internal static class EfOutboxClaimDialectSelector
{
    public static IEfOutboxClaimDialect Select(DbContext context, OpenConnectOutboxClaimStrategy strategy)
    {
        if (strategy is OpenConnectOutboxClaimStrategy.Auto or OpenConnectOutboxClaimStrategy.OptimisticConcurrency)
            return new OptimisticConcurrencyClaimDialect();
        if (strategy == OpenConnectOutboxClaimStrategy.ProviderOptimized && context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            return new SqliteClaimDialect();
        throw new InvalidOperationException($"{OpenConnect.Abstractions.ErrorCodes.EfProviderUnsupported}: provider-optimized outbox claims are unavailable for this EF provider.");
    }
}
