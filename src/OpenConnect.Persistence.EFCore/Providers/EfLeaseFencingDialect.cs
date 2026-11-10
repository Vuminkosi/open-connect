// Postgres single-statement atomic Navigator lease fence (TASK S0-2-FENCING-PG).
//
// Source semantics translated (read-only):
//   - OpenConnect.Navigator.Persistence.Postgres PostgresDurableNavigatorStore.TryAcquireLeaseAsync:
//     single-statement INSERT ... ON CONFLICT (tenant_id, name) DO UPDATE ...
//     WHERE <table>.expires <= now; winner's version = prior + 1 (1 for a new row);
//     losers affect 0 rows and get null.
//   - EF Core mapping onto the EFCore lease model (LeaseEntity, no schema change):
//     (TenantScope, ResourceKey) unique index <-> (tenant_id, name) conflict target.
//     FencingVersion <-> version / fencing token (monotonic, winner-only advance).
//     LeaseEntity.Id (row PK, stable across takeover) <-> LeaseId provenance.
//
// Design notes (deliberate, not silent):
//   - Table/column identifiers are resolved from context.Model (entity-type metadata),
//     never hardcoded, so schema/table-prefix mapping options keep working.
//   - No Npgsql provider package reference: detection is via
//     Database.ProviderName containing "Npgsql"; the statement runs through
//     a DbCommand with DbParameters (named values only).
//     EF1003 note: EF Core 10 analyzer EF1003 flags ExecuteSqlRawAsync calls
//     whose SQL string is built by concatenation. Table/column identifiers
//     here come from ResolveNames() model metadata (not user input) and all
//     values travel as DbParameters, so the DbCommand pattern keeps the
//     analyzer from firing without any warning suppression.
//   - Expiry is floored to whole microseconds (idempotent with the seam's truncation)
//     so persisted expiry never exceeds the requested instant (timestamptz parity).
//   - Fail closed: affected-row-count 0 (live lease / concurrent winner), read-back
//     mismatch, or any non-cancellation failure returns null. Cancellation propagates.

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Navigator;

namespace OpenConnect.Persistence.EFCore.Providers;

internal interface IEfLeaseFencingDialect
{
    string Name { get; }

    ValueTask<NavigatorLease?> TryAcquireAsync(
        DbContext context,
        string tenantScope,
        string executionId,
        string owner,
        DateTimeOffset expiry,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed class PostgresLeaseFencingDialect : IEfLeaseFencingDialect
{
    public static readonly PostgresLeaseFencingDialect Instance = new();

    public string Name => "Postgres";

    public async ValueTask<NavigatorLease?> TryAcquireAsync(
        DbContext context,
        string tenantScope,
        string executionId,
        string owner,
        DateTimeOffset expiry,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(tenantScope)
            || string.IsNullOrWhiteSpace(executionId)
            || string.IsNullOrWhiteSpace(owner))
            return null;
        cancellationToken.ThrowIfCancellationRequested();

        // Idempotent floor: persisted expiry never exceeds the requested instant.
        expiry = new DateTimeOffset(expiry.UtcTicks - expiry.UtcTicks % 10, TimeSpan.Zero);

        try
        {
            ResolveNames(context, out var tableRef, out var tableAlias, out var id, out var tenant, out var resource, out var ownerCol, out var version, out var expires);

            var rowId = Guid.NewGuid();
            var expiryUtc = expiry.UtcDateTime;
            var nowUtc = now.UtcDateTime;

            // EF1003: values are DbParameters; only model-metadata identifiers
            // (tableRef/tableAlias/column names) are concatenated into the text.
            var sql =
                $"INSERT INTO {tableRef} ({id}, {tenant}, {resource}, {ownerCol}, {version}, {expires}) " +
                "VALUES (@id, @tenant, @key, @owner, 1, @expiry) " +
                $"ON CONFLICT ({tenant}, {resource}) DO UPDATE " +
                $"SET {ownerCol} = EXCLUDED.{ownerCol}, {expires} = EXCLUDED.{expires}, {version} = {tableAlias}.{version} + 1 " +
                $"WHERE {tableAlias}.{expires} <= @now";

            var connection = context.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            int changed;
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var currentTransaction = context.Database.CurrentTransaction;
                if (currentTransaction is not null)
                    command.Transaction = currentTransaction.GetDbTransaction();
                AddParameter(command, "@id", DbType.Guid, rowId);
                AddParameter(command, "@tenant", DbType.String, tenantScope);
                AddParameter(command, "@key", DbType.String, executionId);
                AddParameter(command, "@owner", DbType.String, owner);
                AddParameter(command, "@expiry", DbType.DateTime, expiryUtc);
                AddParameter(command, "@now", DbType.DateTime, nowUtc);

                changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!wasOpen)
                    await connection.CloseAsync().ConfigureAwait(false);
            }

            if (changed == 0)
                return null; // Live lease held or concurrent winner: conflict, fail closed.

            var row = await context.Set<LeaseEntity>().AsNoTracking().SingleOrDefaultAsync(
                x => x.TenantScope == tenantScope && x.ResourceKey == executionId,
                cancellationToken).ConfigureAwait(false);

            // Read-back must still show our owner/expiry; otherwise a later winner already
            // overwrote the row between our win and the read. Fail closed (caller retries).
            if (row is null || row.Owner != owner || row.ExpiresUtc != expiryUtc)
                return null;

            // Token monotonicity: the single statement advanced version = prior + 1 (1 for
            // a new row); only the affected-row winner reaches this return.
            return new NavigatorLease(row.Id.ToString("D"), executionId, row.FencingVersion, expiry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    internal static void ResolveNames(
        DbContext context,
        out string tableRef,
        out string tableAlias,
        out string id,
        out string tenant,
        out string resource,
        out string owner,
        out string version,
        out string expires)
    {
        var entityType = context.Model.FindEntityType(typeof(LeaseEntity))
            ?? throw new InvalidOperationException("LeaseEntity is not part of the EF model. ApplyOpenConnectModel must be called from OnModelCreating.");

        var table = entityType.GetTableName() ?? "Leases";
        var schema = entityType.GetSchema();
        tableRef = schema is null ? Quote(table) : Quote(schema) + "." + Quote(table);
        tableAlias = Quote(table);

        string Col(string property) => Quote(entityType.FindProperty(property)?.GetColumnName() ?? property);
        id = Col(nameof(LeaseEntity.Id));
        tenant = Col(nameof(LeaseEntity.TenantScope));
        resource = Col(nameof(LeaseEntity.ResourceKey));
        owner = Col(nameof(LeaseEntity.Owner));
        version = Col(nameof(LeaseEntity.FencingVersion));
        expires = Col(nameof(LeaseEntity.ExpiresUtc));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal static class EfLeaseFencingDialectSelector
{
    public static IEfLeaseFencingDialect? Select(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            return PostgresLeaseFencingDialect.Instance;
        return SqlServerLeaseFencingDialect.TrySelect(context);
    }
}
