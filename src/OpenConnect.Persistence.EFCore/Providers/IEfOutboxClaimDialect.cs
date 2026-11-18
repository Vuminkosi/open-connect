using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        if (request.BatchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request.BatchSize));
        if (string.IsNullOrWhiteSpace(request.LeaseOwner))
            throw new ArgumentException("A lease owner is required.", nameof(request.LeaseOwner));
        if (request.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request.LeaseDuration));

        var now = DateTime.UtcNow;
        // The same owner may always redrive its rows (retry loop); a different owner may only take
        // rows never leased or whose lease expired (fenced handoff). The fencing token and
        // CompleteClaimAsync checks remain the second line of defense.
        var candidates = await context.Set<OutboxEntity>().AsTracking()
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

    internal static async Task<OutboxEntity?> LoadCurrentClaimAsync(DbContext context, OutboxClaim claim, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = await context.Set<OutboxEntity>().FindAsync([Guid.Parse(claim.EntryId)], ct).ConfigureAwait(false);
        if (row is null)
            return null;
        // Claims are tracked for CAS writes, but ownership can change while a
        // previous broker call is in flight. Never trust cached owner/token state.
        await context.Entry(row).ReloadAsync(ct).ConfigureAwait(false);
        if (context.Entry(row).State == EntityState.Detached || row.Status != "pending" ||
            row.LeaseOwner != claim.LeaseOwner || row.FencingVersion != claim.FencingVersion ||
            row.LeaseExpiresUtc is null || row.LeaseExpiresUtc <= DateTime.UtcNow)
            return null;
        return row;
    }

    internal static async Task<bool> CompleteClaimAsync(DbContext context, OutboxClaim claim, CancellationToken ct)
    {
        var row = await LoadCurrentClaimAsync(context, claim, ct).ConfigureAwait(false);
        if (row is null)
            return false;
        return await MutateCurrentClaimAsync(context, claim, row, null, ct).ConfigureAwait(false);
    }

    internal static async Task RetryClaimAsync(DbContext context, OutboxClaim claim, int maxAttempts, CancellationToken ct)
    {
        var row = await LoadCurrentClaimAsync(context, claim, ct).ConfigureAwait(false);
        if (row is not null)
            await MutateCurrentClaimAsync(context, claim, row, maxAttempts, ct).ConfigureAwait(false);
    }

    internal static string DatabaseClock(DbContext context) => context.Database.ProviderName switch
    {
        "Microsoft.EntityFrameworkCore.Sqlite" => "strftime('%Y-%m-%d %H:%M:%f', 'now')",
        "Npgsql.EntityFrameworkCore.PostgreSQL" => "clock_timestamp()",
        "Microsoft.EntityFrameworkCore.SqlServer" => "SYSUTCDATETIME()",
        _ => throw new InvalidOperationException("Atomic outbox lease updates require a supported relational provider.")
    };

    private static async Task<bool> MutateCurrentClaimAsync(DbContext context, OutboxClaim claim, OutboxEntity row, int? maxAttempts, CancellationToken ct)
    {
        // The clock must be evaluated by the database at the mutation, not captured
        // before a network wait. PostgreSQL now() is transaction-stable, so use its
        // wall clock instead. All identifiers come from EF metadata; values are parameters.
        var provider = context.Database.ProviderName;
        var clock = DatabaseClock(context);
        var entity = context.Model.FindEntityType(typeof(OutboxEntity))!;
        var table = entity.GetTableName()!;
        var schema = entity.GetSchema();
        var identifier = context.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>();
        var tableRef = identifier.DelimitIdentifier(table, schema);
        var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, schema);
        string Col(string property) => identifier.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(store)!);
        var values = new List<object> { Guid.Parse(claim.EntryId), claim.LeaseOwner, claim.FencingVersion, claim.TenantScope };
        string assignment;
        if (maxAttempts is null)
        {
            assignment = $"{Col(nameof(OutboxEntity.Status))} = 'delivered', {Col(nameof(OutboxEntity.PublishedUtc))} = {clock}";
        }
        else
        {
            var attempts = row.Attempts + 1;
            values.Add(attempts);
            values.Add(DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)))));
            values.Add(attempts >= maxAttempts ? "quarantined" : "pending");
            assignment = $"{Col(nameof(OutboxEntity.Attempts))} = {{4}}, {Col(nameof(OutboxEntity.NextAttemptUtc))} = {{5}}, " +
                $"{Col(nameof(OutboxEntity.Status))} = {{6}}, {Col(nameof(OutboxEntity.LeaseOwner))} = NULL, {Col(nameof(OutboxEntity.LeaseExpiresUtc))} = NULL";
        }
        var expiryColumn = Col(nameof(OutboxEntity.LeaseExpiresUtc));
        // Compare SQLite timestamps at the database clock's own precision rather
        // than lexically comparing variable-length fractional-second strings.
        var liveExpiry = provider == "Microsoft.EntityFrameworkCore.Sqlite"
            ? $"julianday({expiryColumn}) > julianday('now')"
            : $"{expiryColumn} > {clock}";
        var sql = $"UPDATE {tableRef} SET {assignment} WHERE {Col(nameof(OutboxEntity.Id))} = {{0}} " +
            $"AND {Col(nameof(OutboxEntity.LeaseOwner))} = {{1}} AND {Col(nameof(OutboxEntity.FencingVersion))} = {{2}} " +
            $"AND {Col(nameof(OutboxEntity.TenantScope))} = {{3}} AND {Col(nameof(OutboxEntity.Status))} = 'pending' " +
            $"AND {liveExpiry}";
        ct.ThrowIfCancellationRequested();
        try
        {
            return await context.Database.ExecuteSqlRawAsync(sql, values, ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            // Direct SQL bypasses tracking. Do not leave a cached row available for
            // a later SaveChanges to overwrite a winner or the newly persisted result.
            context.Entry(row).State = EntityState.Detached;
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
