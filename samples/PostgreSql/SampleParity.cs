using Microsoft.EntityFrameworkCore;
using OpenConnect.Samples.PostgreSql;

namespace OpenConnect.Samples.PostgreSql;

/// <summary>
/// Offline (no connection, no seeding) legacy-parity probe for the PostgreSQL sample.
/// Asserts the hardened adapter model retains all eight legacy tables and adds exactly
/// the four hardened tables — the parity matrix this sample proves.
/// </summary>
public static class SampleParity
{
    private static readonly string[] LegacyTables =
    [
        "Sessions", "Tasks", "Turns", "Events",
        "Outbox", "Leases", "Approvals", "PrivacyTokens",
    ];

    private static readonly string[] HardenedOnlyTables =
    [
        "OperationalRuns", "NativeAgentSessions",
        "PublicSessionMappings", "ApprovalNonces",
    ];

    /// <summary>
    /// Reports the legacy-to-hardened table parity matrix from the EF Core model.
    /// Opens no connection and writes no data.
    /// </summary>
    /// <param name="db">The sample DbContext (model inspection only).</param>
    /// <returns>0 when parity holds; 1 with <c>sample_parity_failed</c> otherwise.</returns>
    public static int ReportParity(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        var mapped = db.Model.GetEntityTypes()
            .Select(t => (Schema: t.GetSchema(), Table: t.GetTableName()))
            .Where(t => !string.IsNullOrEmpty(t.Table))
            .OrderBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Mapped tables ({mapped.Count}), schema-qualified:");
        foreach (var (schema, table) in mapped)
        {
            var tag = LegacyTables.Contains(table, StringComparer.Ordinal)
                ? "retained-legacy"
                : HardenedOnlyTables.Contains(table, StringComparer.Ordinal)
                    ? "hardened-only"
                    : "UNEXPECTED";
            Console.WriteLine($"  - {schema}.{table} [{tag}]");
        }

        var tables = mapped
            .Where(t => string.Equals(t.Schema, PostgreSqlSampleDbContext.HistorySchema, StringComparison.Ordinal))
            .Select(t => t.Table!)
            .ToHashSet(StringComparer.Ordinal);

        var missingLegacy = LegacyTables.Where(t => !tables.Contains(t)).ToArray();
        var missingHardened = HardenedOnlyTables.Where(t => !tables.Contains(t)).ToArray();
        var unexpected = tables
            .Except(LegacyTables, StringComparer.Ordinal)
            .Except(HardenedOnlyTables, StringComparer.Ordinal)
            .ToArray();

        if (missingLegacy.Length == 0 && missingHardened.Length == 0 && unexpected.Length == 0)
        {
            Console.WriteLine(
                $"Parity OK: 8 legacy tables retained + 4 hardened tables in schema '{PostgreSqlSampleDbContext.HistorySchema}'.");
            return 0;
        }

        Console.Error.WriteLine("sample_parity_failed: hardened model does not match the parity matrix.");
        foreach (var missing in missingLegacy)
        {
            Console.Error.WriteLine($"  missing legacy table: {missing}");
        }

        foreach (var missing in missingHardened)
        {
            Console.Error.WriteLine($"  missing hardened table: {missing}");
        }

        foreach (var extra in unexpected)
        {
            Console.Error.WriteLine($"  unexpected table: {extra}");
        }

        return 1;
    }
}
