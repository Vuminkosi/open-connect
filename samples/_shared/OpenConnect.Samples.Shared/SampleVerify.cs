using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Samples.Shared;

/// <summary>
/// Provider-neutral verification helpers for samples. Uses only the EF Core model and
/// connection probes; contains no provider-specific SQL.
/// </summary>
public static class SampleVerify
{
    /// <summary>
    /// Verifies the database is reachable and reports the mapped OpenConnect tables.
    /// </summary>
    /// <param name="db">The sample DbContext.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of mapped entity tables.</returns>
    public static async Task<int> VerifyModelAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("sample_verify_failed: database is not reachable. Run the 'migrate' verb first.");
        }

        var tables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Connected. Mapped OpenConnect tables ({tables.Count}):");
        foreach (var table in tables)
        {
            Console.WriteLine($"  - {table}");
        }

        return tables.Count;
    }

    /// <summary>
    /// Reports applied vs pending EF Core migrations through the provider-neutral API.
    /// </summary>
    /// <param name="db">The sample DbContext.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of applied migrations.</returns>
    public static async Task<int> VerifyMigrationsAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);

        var appliedList = applied.ToList();
        var pendingList = pending.ToList();

        Console.WriteLine($"Applied migrations ({appliedList.Count}):");
        foreach (var migration in appliedList)
        {
            Console.WriteLine($"  - {migration}");
        }

        if (pendingList.Count > 0)
        {
            throw new InvalidOperationException(
                $"sample_verify_failed: {pendingList.Count} migration(s) pending ({string.Join(", ", pendingList)}). Run the 'migrate' verb first.");
        }

        return appliedList.Count;
    }
}
