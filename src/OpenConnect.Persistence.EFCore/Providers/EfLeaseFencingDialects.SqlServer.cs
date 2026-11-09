// SQL Server single-statement atomic lease-acquire dialect (TASK S0-3-FENCING-MSSQL).
//
// Contract: IEfLeaseFencingDialect (Providers/EfLeaseFencingDialect.cs, S0-2-owned).
// Shape confirmed by read-only inspection of the parallel S0-2 working copy; this file
// references that contract but never edits it. If S0-2's shape moves, the
// migration-integration task reconciles; this file's member signatures already match.
//
// Reference semantics translated (read-only):
//   - PostgresDurableNavigatorStore.TryAcquireLeaseAsync: single-statement
//     INSERT ... ON CONFLICT ... DO UPDATE WHERE expires_at <= now; new row token 1,
//     takeover prior + 1; 0 affected rows -> null.
//   - PostgresLeaseFencingDialect: metadata-resolved identifiers, microsecond expiry
//     floor, read-back owner/expiry verification, fail closed (non-cancellation -> null).
//
// SQL Server translation (single statement, strictly stronger read-back):
//   MERGE INTO <leases> WITH (HOLDLOCK) ... WHEN MATCHED AND expires <= now THEN UPDATE
//   (owner/expiry replaced, FencingVersion = prior + 1) WHEN NOT MATCHED THEN INSERT
//   (new Id, token literal 1) OUTPUT INSERTED.Id, INSERTED.FencingVersion.
//   HOLDLOCK serializes the match/insert decision; OUTPUT returns the winner's row from
//   the same statement, so no separate read-back race exists.
//   Residual unique-index race (2601/2627) -> null; any other non-cancellation failure
//   -> null (fail closed, mirroring the Postgres sibling).
//
// Parallel-safety: this file is the ONLY owned path for S0-3. It does NOT edit
// Providers/EfLeaseFencingDialect.cs (S0-2) or Navigator/EfNavigatorLeaseSeam.cs.
// Selector wiring (NOT applied here; one-line patch for the S0-2 selector file):
//   ... ?? SqlServerLeaseFencingDialect.TrySelect(context)
// The TrySelect helper below packages exactly that branch so integration is mechanical.
//
// NOT VERIFIED (verification lane, never assumed):
//   - Single-statement atomicity under concurrent MERGE on a real SQL Server container
//     (parallel acquirers, takeover race, live-lease conflict; e.g. mssql-testcontainer
//     with N tasks asserting exactly one winner and monotonic tokens).
//   - Contract conformance if S0-2's working-copy shape changes before merge.

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Navigator;

namespace OpenConnect.Persistence.EFCore.Providers;

/// <summary>
/// SQL Server lease-acquire dialect: single-statement atomic <c>MERGE WITH (HOLDLOCK)</c>.
/// Stateless; use <see cref="Instance"/> or <see cref="TrySelect"/>.
/// </summary>
internal sealed class SqlServerLeaseFencingDialect : IEfLeaseFencingDialect
{
    public static readonly SqlServerLeaseFencingDialect Instance = new();

    public string Name => "SqlServer";

    /// <summary>
    /// Provider probe for the selector branch. Mirrors the SqlServer detection convention
    /// in <c>OpenConnectModelCacheKeyFactory</c> (provider name contains "SqlServer").
    /// </summary>
    public static bool IsSqlServer(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Selector branch hook: returns the dialect when the context targets SQL Server,
    /// otherwise null so the selector falls through to the next branch.
    /// </summary>
    public static IEfLeaseFencingDialect? TrySelect(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsSqlServer(context) ? Instance : null;
    }

    /// <summary>
    /// Single-statement atomic acquire. Returns the winning lease (new row token 1,
    /// takeover prior + 1 with the stable row Id) or null on conflict/failure.
    /// Only cancellation and null-argument guards throw; all else fails closed.
    /// The caller must not use the <see cref="DbContext"/> concurrently.
    /// </summary>
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

        // Idempotent floor (same as the Postgres sibling): persisted expiry never exceeds
        // the requested instant (datetime2 parity with timestamptz truncation).
        expiry = new DateTimeOffset(expiry.UtcTicks - expiry.UtcTicks % 10, TimeSpan.Zero);

        try
        {
            if (!TryResolveLeaseMapping(
                    context,
                    out var table,
                    out var colId,
                    out var colTenant,
                    out var colKey,
                    out var colOwner,
                    out var colVersion,
                    out var colExpires))
                return null;

            var rowId = Guid.NewGuid();
            var expiryUtc = expiry.UtcDateTime;
            var nowUtc = now.UtcDateTime;

            // The expiry predicate is the fence takeover gate: only an expired (or
            // absent) row lets this statement write. A live row yields 0 OUTPUT rows.
            var sql = $"""
                MERGE INTO {table} WITH (HOLDLOCK) AS [target]
                USING (SELECT @tenant AS {colTenant}, @key AS {colKey}) AS [source]
                ON [target].{colTenant} = [source].{colTenant} AND [target].{colKey} = [source].{colKey}
                WHEN MATCHED AND [target].{colExpires} <= @now THEN
                UPDATE SET {colOwner} = @owner, {colExpires} = @expiry, {colVersion} = [target].{colVersion} + 1
                WHEN NOT MATCHED THEN
                INSERT ({colId}, {colTenant}, {colKey}, {colOwner}, {colVersion}, {colExpires})
                VALUES (@id, @tenant, @key, @owner, 1, @expiry)
                OUTPUT INSERTED.{colId}, INSERTED.{colVersion};
                """;

            var connection = context.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var currentTransaction = context.Database.CurrentTransaction;
                if (currentTransaction is not null)
                    command.Transaction = currentTransaction.GetDbTransaction();
                AddParameter(command, "@tenant", DbType.String, tenantScope);
                AddParameter(command, "@key", DbType.String, executionId);
                AddParameter(command, "@owner", DbType.String, owner);
                AddParameter(command, "@now", DbType.DateTime2, nowUtc);
                AddParameter(command, "@expiry", DbType.DateTime2, expiryUtc);
                AddParameter(command, "@id", DbType.Guid, rowId);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return null; // Live lease held or concurrent winner: conflict, fail closed.
                var winnerId = reader.GetGuid(0);
                var fencingToken = reader.GetInt64(1);

                // Token monotonicity: the single statement wrote token 1 (new row) or
                // prior + 1 (takeover); only the OUTPUT-row winner reaches this return.
                return new NavigatorLease(winnerId.ToString("D"), executionId, fencingToken, expiry);
            }
            finally
            {
                if (!wasOpen)
                    await connection.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Residual unique-index race loser (2601/2627): another process won. Fail closed.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed, mirroring the Postgres sibling: no partial state escapes.
            return null;
        }
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool TryResolveLeaseMapping(
        DbContext context,
        out string table,
        out string colId,
        out string colTenant,
        out string colKey,
        out string colOwner,
        out string colVersion,
        out string colExpires)
    {
        table = colId = colTenant = colKey = colOwner = colVersion = colExpires = string.Empty;
        var entityType = context.Model.FindEntityType(typeof(LeaseEntity));
        if (entityType is null)
            return false;
        var tableName = entityType.GetTableName();
        if (string.IsNullOrEmpty(tableName))
            return false;
        var schema = entityType.GetSchema();
        table = string.IsNullOrEmpty(schema)
            ? Quote(tableName)
            : $"{Quote(schema)}.{Quote(tableName)}";
        return TryColumn(entityType, nameof(LeaseEntity.Id), out colId)
            && TryColumn(entityType, nameof(LeaseEntity.TenantScope), out colTenant)
            && TryColumn(entityType, nameof(LeaseEntity.ResourceKey), out colKey)
            && TryColumn(entityType, nameof(LeaseEntity.Owner), out colOwner)
            && TryColumn(entityType, nameof(LeaseEntity.FencingVersion), out colVersion)
            && TryColumn(entityType, nameof(LeaseEntity.ExpiresUtc), out colExpires);
    }

    private static bool TryColumn(
        Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType entityType,
        string property,
        out string column)
    {
        var columnName = entityType.FindProperty(property)?.GetColumnName();
        if (string.IsNullOrEmpty(columnName))
        {
            column = string.Empty;
            return false;
        }
        column = Quote(columnName);
        return true;
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    // Repo convention (EfApprovalNonceStore / EfNativeAgentSessionStore /
    // EfPublicSessionMappingStore): provider-reported unique violations only. Walks the
    // exception itself plus inners because direct ADO.NET surfaces SqlException unwrapped
    // (ExecuteSql-style paths would see it wrapped in DbUpdateException; both are covered).
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var code = type.GetProperty("Number")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.SqlClient.SqlException" && (code == "2601" || code == "2627"))
                return true;
        }
        return false;
    }
}
