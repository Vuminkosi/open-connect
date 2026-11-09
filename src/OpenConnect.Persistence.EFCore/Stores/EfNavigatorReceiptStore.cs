using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Navigator;

namespace OpenConnect.Persistence.EFCore.Stores;

// EF Core transplant of the legacy Postgres receipt paths
// (PostgresDurableNavigatorStore.ConsumeApprovalAndSaveReceiptAsync :93-138,
// GetReceiptAsync, GetReceiptAsync/TrySaveReceiptAsync operational).
//
// This project carries no Navigator assembly reference by design (same
// reason EfNavigatorLeaseSeam defines its own INavigatorLeaseSeam types),
// so this store exposes store-local contract types whose names and JSON
// shape map 1:1 onto the legacy durable contracts:
//   EfNavigatorReceiptOutcome <-> NavigatorStoreOutcome subset
//     (Succeeded, NotFound, Conflict, Expired, AlreadyConsumed).
//   EfNavigatorReceiptResult  <-> NavigatorStoreResult(Outcome, IsReplay).
//   EfNavigatorReceiptInput/View <-> NavigatorReceiptRecord
//     (CommandId, IdempotencyKey, Digest, Result, ExpiresAt, FencingToken;
//     serialized property order is identical, so rows remain readable
//     across stores).
//   EfOperationalReceiptInput/View <-> OperationalReceipt (without IsReplay;
//     replays preserve the original execution id and fencing token as
//     provenance, never as a new execution).
// A thin adapter outside this project maps these onto IDurableNavigatorStore
// / IDurableOperationalStore; DI registration for that adapter is proposed
// by the owning slice, not added here.
//
// TenantScope is the canonical Guid "D" string on every receipt/approval row,
// matching EfNavigatorLeaseSeam.TrySplitExecution and NavigatorReceiptMapping.
// Non-UUID tenant/principal/session inputs fail closed (Conflict/null/false);
// callers keep their ownership checks. No session, event, or lease behavior
// lives here; this store owns approvals + receipt tasks only (OC-PERSIST-001,
// additive only: S0-1 TaskEntity receipt columns, S0-4 ApprovalEntity command
// columns).
//
// Divergences from the siblings, documented (not silent):
//   - Replay identity follows the Postgres triple (CommandId, Digest,
//     IdempotencyKey), NOT the in-memory full-record equality (which also
//     compares Result, ExpiresAt, and FencingToken). Result/FencingToken
//     persist opaquely inside the JSON payload and are not part of the
//     conflict decision.
//   - Postgres timestamptz stores microseconds; persisted instants are
//     floor-truncated to whole microseconds so expiry is never extended
//     by rounding (TruncateToMicroseconds, applied to now and ExpiresAt).
//   - String comparisons use provider collation via LINQ ==, as do the
//     other EF stores in this project; Postgres text = is ordinal.
//   - Read-modify-write under a single transaction with a unique-index
//     race-resolution re-read (see method remarks), not a single atomic
//     statement; concurrent losers resolve via the committed receipt.

/// <summary>Consume outcome mirroring the legacy durable store subset.</summary>
public enum EfNavigatorReceiptOutcome
{
    Succeeded,
    NotFound,
    Conflict,
    Expired,
    AlreadyConsumed,
}

/// <summary>Consume result mirroring NavigatorStoreResult.</summary>
public sealed record EfNavigatorReceiptResult(EfNavigatorReceiptOutcome Outcome, bool IsReplay = false);

/// <summary>Receipt input mirroring NavigatorReceiptRecord (JSON-identical).</summary>
public sealed record EfNavigatorReceiptInput(
    string CommandId,
    string IdempotencyKey,
    string Digest,
    JsonElement Result,
    DateTimeOffset ExpiresAt,
    long FencingToken);

/// <summary>Receipt view mirroring NavigatorReceiptRecord (JSON-identical).</summary>
public sealed record EfNavigatorReceiptView(
    string CommandId,
    string IdempotencyKey,
    string Digest,
    JsonElement Result,
    DateTimeOffset ExpiresAt,
    long FencingToken);

/// <summary>Operational run status mirroring OperationalRunStatus.</summary>
public enum EfOperationalReceiptStatus
{
    Completed,
    Cancelled,
    Failed,
    Uncertain,
    LeaseLost,
    Rejected,
}

/// <summary>Operational receipt input mirroring OperationalReceipt.</summary>
public sealed record EfOperationalReceiptInput(
    string ExecutionId,
    EfOperationalReceiptStatus Status,
    int CompletedSteps,
    string? SafeSummary,
    long FencingToken);

/// <summary>Operational receipt view mirroring OperationalReceipt.</summary>
public sealed record EfOperationalReceiptView(
    string ExecutionId,
    EfOperationalReceiptStatus Status,
    int CompletedSteps,
    string? SafeSummary,
    long FencingToken);

public sealed class EfNavigatorReceiptStore<TContext>(TContext context)
    where TContext : DbContext
{
    /// <summary>
    /// Consumes a pending approval and persists the navigator receipt in one
    /// transaction, mirroring the legacy Postgres consume path.
    /// </summary>
    /// <remarks>
    /// Order per legacy :93-138: scope gate, session check, receipt pre-read
    /// (replay match / corrupt Conflict / expired Expired / triple-mismatch
    /// Conflict), approval load (NotFound / Expired / digest-or-state-or-
    /// consumed AlreadyConsumed), mark consumed, ensure the parent session
    /// task row, insert the receipt task row, commit. A unique-index insert
    /// loser rolls back and re-reads the committed receipt, returning replay
    /// only on a triple match, else Conflict. Any other database failure is
    /// Conflict (fail closed).
    /// </remarks>
    public async ValueTask<EfNavigatorReceiptResult> ConsumeApprovalAndSaveReceiptAsync(
        string id,
        string tenant,
        string principal,
        string commandId,
        string digest,
        DateTimeOffset now,
        EfNavigatorReceiptInput receipt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt is null
            || string.IsNullOrWhiteSpace(receipt.IdempotencyKey)
            || !TryScope(id, tenant, principal, out var sid, out var tenantId, out var principalId, out var tenantScope, out var principalScope))
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Conflict);

        var receiptKey = NavigatorReceiptMapping.ReceiptClientTaskId(receipt.IdempotencyKey);

        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sessionExists = await context.Set<SessionEntity>().AsNoTracking()
            .AnyAsync(x => x.SessionId == sid && x.TenantScope == tenantScope && x.PrincipalScope == principalScope, cancellationToken)
            .ConfigureAwait(false);
        if (!sessionExists)
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.NotFound);

        var existing = await context.Set<TaskEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantScope == tenantScope && x.PrincipalScope == principalScope && x.ClientTaskId == receiptKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.ReceiptPayload is not null)
        {
            var stored = ReadReceipt(existing.ReceiptPayload);
            if (stored is null)
                return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Conflict);
            if (stored.ExpiresAt <= now)
                return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Expired);
            if (!string.Equals(stored.CommandId, commandId, StringComparison.Ordinal)
                || !string.Equals(stored.Digest, digest, StringComparison.Ordinal)
                || !string.Equals(stored.IdempotencyKey, receipt.IdempotencyKey, StringComparison.Ordinal))
                return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Conflict);
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Succeeded, true);
        }

        var approval = await context.Set<ApprovalEntity>()
            .SingleOrDefaultAsync(x => x.TenantScope == tenantScope && x.PrincipalScope == principalScope && x.TaskId == sid && x.CommandId == commandId, cancellationToken)
            .ConfigureAwait(false);
        if (approval is null)
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.NotFound);
        if (approval.ExpiresUtc <= now.UtcDateTime)
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Expired);
        if (!string.Equals(approval.CommandDigest, digest, StringComparison.Ordinal)
            || !string.Equals(approval.State, "pending", StringComparison.Ordinal)
            || approval.ConsumedAtUtc is not null)
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.AlreadyConsumed);

        var persistedNow = TruncateToMicroseconds(now);
        approval.ConsumedAtUtc = persistedNow.UtcDateTime;
        approval.State = "consumed";

        // The receipt task references its session task; ensure the parent
        // exists in this transaction before inserting it (legacy parity).
        var sessionTaskExists = await context.Set<TaskEntity>().AsNoTracking()
            .AnyAsync(x => x.TaskId == sid && x.TenantScope == tenantScope && x.PrincipalScope == principalScope, cancellationToken)
            .ConfigureAwait(false);
        if (!sessionTaskExists)
        {
            context.Set<TaskEntity>().Add(new TaskEntity
            {
                TaskId = sid,
                TenantScope = tenantScope,
                PrincipalScope = principalScope,
                SessionId = sid,
                ClientTaskId = $"navigator:{sid:N}",
                WorkloadScope = string.Empty,
                AgentId = "navigator",
                State = "Accepted",
                Version = 1,
                CanonicalHash = string.Empty,
                CreatedUtc = persistedNow.UtcDateTime,
            });
        }

        var persistedReceipt = receipt with { ExpiresAt = TruncateToMicroseconds(receipt.ExpiresAt) };
        context.Set<TaskEntity>().Add(new TaskEntity
        {
            TaskId = NavigatorReceiptMapping.ReceiptTaskId(tenantId, principalId, receipt.IdempotencyKey),
            TenantScope = tenantScope,
            PrincipalScope = principalScope,
            SessionId = sid,
            ClientTaskId = receiptKey,
            WorkloadScope = string.Empty,
            AgentId = "navigator",
            State = "completed",
            Version = 1,
            CanonicalHash = digest,
            CreatedUtc = persistedNow.UtcDateTime,
            CompletedUtc = persistedNow.UtcDateTime,
            ReceiptStatus = "Succeeded",
            ReceiptPayload = JsonSerializer.Serialize(persistedReceipt),
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Succeeded);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            // A concurrent consumer may have committed the unique receipt
            // after our pre-read. Resolve the race by reading the committed
            // receipt and returning replay only on a triple match.
            var committed = await GetReceiptAsync(tenant, principal, receipt.IdempotencyKey, now, cancellationToken).ConfigureAwait(false);
            if (committed is not null
                && string.Equals(committed.CommandId, commandId, StringComparison.Ordinal)
                && string.Equals(committed.Digest, digest, StringComparison.Ordinal)
                && string.Equals(committed.IdempotencyKey, receipt.IdempotencyKey, StringComparison.Ordinal))
                return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Succeeded, true);
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Conflict);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return new EfNavigatorReceiptResult(EfNavigatorReceiptOutcome.Conflict);
        }
    }

    /// <summary>
    /// Looks up a navigator receipt; expired or corrupt rows read as absent
    /// (fail closed). No expiry gate applies to the operational overload.
    /// </summary>
    public async ValueTask<EfNavigatorReceiptView?> GetReceiptAsync(
        string tenant,
        string principal,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || !Guid.TryParse(tenant, out var tenantId)
            || !Guid.TryParse(principal, out var principalId))
            return null;

        var tenantScope = tenantId.ToString("D");
        var principalScope = principalId.ToString("D");
        var receiptKey = NavigatorReceiptMapping.ReceiptClientTaskId(idempotencyKey);
        var row = await context.Set<TaskEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantScope == tenantScope && x.PrincipalScope == principalScope && x.ClientTaskId == receiptKey, cancellationToken)
            .ConfigureAwait(false);
        var value = row?.ReceiptPayload is null ? null : ReadReceipt(row.ReceiptPayload);
        return value is { ExpiresAt: var expires } && expires > now ? value : null;
    }

    /// <summary>
    /// Tenant-only operational receipt read (PrincipalScope equals
    /// TenantScope by contract). Corrupt payloads read as absent; no
    /// expiry gate on get (legacy parity).
    /// </summary>
    public async ValueTask<EfOperationalReceiptView?> GetOperationalReceiptAsync(
        string tenantScope,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || !Guid.TryParse(tenantScope, out var tenant))
            return null;

        var scope = tenant.ToString("D");
        var row = await context.Set<TaskEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantScope == scope && x.PrincipalScope == scope && x.ClientTaskId == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (row?.ReceiptPayload is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<EfOperationalReceiptView>(row.ReceiptPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Persists an operational receipt once. Rejects non-completed
    /// outcomes, blank keys, and duplicate keys (false, fail closed);
    /// insert races also return false.
    /// </summary>
    public async ValueTask<bool> TrySaveOperationalReceiptAsync(
        string tenantScope,
        string idempotencyKey,
        EfOperationalReceiptInput receipt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || receipt is null
            || receipt.Status != EfOperationalReceiptStatus.Completed
            || !Guid.TryParse(tenantScope, out var tenant))
            return false;

        var scope = tenant.ToString("D");
        var tasks = context.Set<TaskEntity>();
        var duplicate = await tasks.AsNoTracking()
            .AnyAsync(x => x.TenantScope == scope && x.PrincipalScope == scope && x.ClientTaskId == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
            return false;

        var id = NavigatorReceiptMapping.ReceiptTaskId(tenant, tenant, idempotencyKey);
        var session = await context.Set<SessionEntity>()
            .SingleOrDefaultAsync(x => x.SessionId == id && x.TenantScope == scope && x.PrincipalScope == scope, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            context.Set<SessionEntity>().Add(new SessionEntity
            {
                SessionId = id,
                TenantScope = scope,
                PrincipalScope = scope,
                Version = 1,
                CreatedUtc = completedAt.UtcDateTime,
                UpdatedUtc = completedAt.UtcDateTime,
            });
        }

        tasks.Add(new TaskEntity
        {
            TaskId = id,
            TenantScope = scope,
            PrincipalScope = scope,
            SessionId = id,
            ClientTaskId = idempotencyKey,
            WorkloadScope = string.Empty,
            AgentId = "navigator-operational",
            State = "completed",
            Version = 1,
            CanonicalHash = string.Empty,
            CreatedUtc = completedAt.UtcDateTime,
            CompletedUtc = completedAt.UtcDateTime,
            ReceiptStatus = "Succeeded",
            ReceiptPayload = JsonSerializer.Serialize(receipt),
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            return false;
        }
    }

    private static bool TryScope(string id, string tenant, string principal, out Guid sid, out Guid tenantId, out Guid principalId, out string tenantScope, out string principalScope)
    {
        sid = Guid.Empty;
        tenantId = Guid.Empty;
        principalId = Guid.Empty;
        tenantScope = string.Empty;
        principalScope = string.Empty;
        if (!Guid.TryParse(id, out sid)
            || !Guid.TryParse(tenant, out tenantId)
            || !Guid.TryParse(principal, out principalId))
            return false;
        tenantScope = tenantId.ToString("D");
        principalScope = principalId.ToString("D");
        return true;
    }

    private static EfNavigatorReceiptView? ReadReceipt(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<EfNavigatorReceiptView>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Floor to whole microseconds, mirroring Postgres timestamptz storage so
    // persisted expiry never exceeds the requested instant (legacy parity).
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);

    // Keep this narrow: only a provider-reported unique/primary-key violation
    // is a create race (ported from EfApprovalNonceStore).
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var code = type.GetProperty("SqliteExtendedErrorCode")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.Sqlite.SqliteException" && (code == "1555" || code == "2067"))
                return true;
            code = type.GetProperty("SqlState")?.GetValue(current)?.ToString();
            if (type.FullName == "Npgsql.PostgresException" && code == "23505")
                return true;
            code = type.GetProperty("Number")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.SqlClient.SqlException" && (code == "2601" || code == "2627"))
                return true;
        }

        return false;
    }
}
