using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>PostgreSQL-backed, tenant-scoped durable Navigator store.</summary>
public sealed class PostgresDurableNavigatorStore(OpenConnectDbContext db) : IDurableNavigatorStore, IDurableOperationalStore
{
    private const string CheckpointKey = "navigator";
    public bool IsDurable => true;

    public async ValueTask<NavigatorSession?> GetSessionAsync(string id, string tenant, string principal, CancellationToken cancellationToken = default)
    {
        if (!Scope(id, tenant, principal, out var sessionId, out var tenantId, out var principalId)) return null;
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == tenantId && x.OwnerId == principalId, cancellationToken);
        if (row?.ContextCheckpoint is null) return null;
        try
        {
            var navigator = JsonNode.Parse(row.ContextCheckpoint)?[CheckpointKey];
            if (navigator is null) return null;
            var state = Enum.Parse<NavigatorSessionState>(navigator["state"]!.GetValue<string>(), false);
            return NavigatorSession.Restore(new NavigatorSessionSnapshot(id, tenant, principal, state,
                navigator["clientInstance"]!.GetValue<string>(), navigator["routeId"]!.GetValue<string>(), navigator["revision"]!.GetValue<long>()));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or FormatException or NullReferenceException) { return null; }
    }

    public async ValueTask<NavigatorStoreOutcome> UpdateSessionSnapshotAsync(string id, string tenant, string principal, NavigatorSessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (snapshot is null) return NavigatorStoreOutcome.Conflict;
        try { snapshot.Validate(); }
        catch (ArgumentException) { return NavigatorStoreOutcome.Conflict; }
        if (!Scope(id, tenant, principal, out var sid, out var tid, out var pid) || snapshot.SessionId != id || snapshot.TenantId != tenant || snapshot.PrincipalId != principal)
            return NavigatorStoreOutcome.Conflict;
        var row = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sid && x.TenantId == tid && x.OwnerId == pid, cancellationToken);
        if (row is null) return NavigatorStoreOutcome.NotFound;
        JsonObject root;
        try { root = JsonNode.Parse(row.ContextCheckpoint ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return NavigatorStoreOutcome.Conflict; }
        var existingRevision = root[CheckpointKey]?["revision"]?.GetValue<long>();
        if (existingRevision is null) return NavigatorStoreOutcome.Conflict;
        if (existingRevision != expectedRevision || snapshot.Revision != expectedRevision + 1) return NavigatorStoreOutcome.StaleFence;
        root[CheckpointKey] = new JsonObject { ["state"] = snapshot.State.ToString(), ["clientInstance"] = snapshot.ClientInstance, ["routeId"] = snapshot.RouteId, ["revision"] = snapshot.Revision };
        var updatedAt = DateTimeOffset.UtcNow;
        row.ContextCheckpoint = root.ToJsonString();
        row.Version = checked(row.Version + 1);
        row.UpdatedAt = updatedAt;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return NavigatorStoreOutcome.Succeeded;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
            return NavigatorStoreOutcome.StaleFence;
        }
    }

    public async ValueTask<string> CreateSessionAsync(string tenant, string principal, string clientInstance, string routeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NavigatorPersistenceMapping.TryUuid(tenant, out var t) || !NavigatorPersistenceMapping.TryUuid(principal, out var p)) throw new InvalidOperationException("Persistence scope must use UUID identifiers.");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var checkpoint = new JsonObject { [CheckpointKey] = new JsonObject { ["state"] = NavigatorSessionState.Created.ToString(), ["clientInstance"] = clientInstance, ["routeId"] = routeId, ["revision"] = 0 } }.ToJsonString();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Sessions.Add(new SessionEntity { Id = id, TenantId = t, OwnerId = p, Version = 1, ContextCheckpoint = checkpoint, CreatedAt = now, UpdatedAt = now });
        db.Tasks.Add(SessionTask(id, t, p, now));
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return id.ToString("D");
    }

    public async ValueTask<NavigatorStoreOutcome> CreatePendingApprovalAsync(string id, string tenant, string principal, NavigatorApprovalRecord approval, CancellationToken cancellationToken = default)
    {
        if (!Scope(id, tenant, principal, out var sid, out var t, out var p)) return NavigatorStoreOutcome.Conflict;
        var session = await db.Sessions.AsNoTracking().AnyAsync(x => x.Id == sid && x.TenantId == t && x.OwnerId == p, cancellationToken);
        if (!session) return NavigatorStoreOutcome.NotFound;
        var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid && x.TenantId == t && x.PrincipalId == p, cancellationToken);
        if (task is null) { task = SessionTask(sid,t,p,DateTimeOffset.UtcNow); db.Tasks.Add(task); }
        db.Approvals.Add(new ApprovalEntity { Id = Guid.NewGuid(), TenantId=t, PrincipalId=p, TaskId=sid, CommandId=approval.CommandId, CommandDigest=approval.Digest, ExpiresAt=approval.ExpiresAt, State="pending", CreatedAt=DateTimeOffset.UtcNow });
        try { await db.SaveChangesAsync(cancellationToken); return NavigatorStoreOutcome.Succeeded; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return NavigatorStoreOutcome.Conflict; }
    }

    public async ValueTask<NavigatorStoreResult> ConsumeApprovalAndSaveReceiptAsync(string id, string tenant, string principal, string commandId, string digest, DateTimeOffset now, NavigatorReceiptRecord receipt, CancellationToken cancellationToken = default)
    {
        if (!Scope(id, tenant, principal, out var sid, out var t, out var p)) return new(NavigatorStoreOutcome.Conflict);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.Sessions.AsNoTracking().AnyAsync(x => x.Id == sid && x.TenantId == t && x.OwnerId == p, cancellationToken);
        if (!session) return new(NavigatorStoreOutcome.NotFound);
        var existing = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == t && x.PrincipalId == p && x.ClientTaskId == NavigatorPersistenceMapping.ReceiptClientTaskId(receipt.IdempotencyKey), cancellationToken);
        if (existing?.ReceiptPayload is not null)
        {
            var stored = ReadReceipt(existing.ReceiptPayload);
            if (stored is null) return new(NavigatorStoreOutcome.Conflict);
            if (stored.ExpiresAt <= now) return new(NavigatorStoreOutcome.Expired);
            if (stored.CommandId != commandId || stored.Digest != digest || stored.IdempotencyKey != receipt.IdempotencyKey) return new(NavigatorStoreOutcome.Conflict);
            return new(NavigatorStoreOutcome.Succeeded, true);
        }
        var approval = await db.Approvals.SingleOrDefaultAsync(x => x.TenantId == t && x.PrincipalId == p && x.TaskId == sid && x.CommandId == commandId, cancellationToken);
        if (approval is null) return new(NavigatorStoreOutcome.NotFound);
        if (approval.ExpiresAt <= now) return new(NavigatorStoreOutcome.Expired);
        if (approval.CommandDigest != digest || approval.State != "pending" || approval.ConsumedAt is not null) return new(NavigatorStoreOutcome.AlreadyConsumed);
        approval.ConsumedAt = TruncateToMicroseconds(now); approval.State = "consumed";
        // Receipt TaskEntity references its session task through the composite task/session
        // relationship. Ensure the parent exists in this transaction before inserting it.
        var sessionTask = await db.Tasks.SingleOrDefaultAsync(x => x.Id == sid && x.TenantId == t && x.PrincipalId == p, cancellationToken);
        if (sessionTask is null)
        {
            db.Tasks.Add(SessionTask(sid, t, p, now));
        }
        var taskId = NavigatorPersistenceMapping.ReceiptTaskId(t,p,receipt.IdempotencyKey);
        var persistedNow = TruncateToMicroseconds(now);
        var persistedReceipt = receipt with { ExpiresAt = TruncateToMicroseconds(receipt.ExpiresAt) };
        db.Tasks.Add(new TaskEntity { Id=taskId, TenantId=t, PrincipalId=p, SessionId=sid, ClientTaskId=NavigatorPersistenceMapping.ReceiptClientTaskId(receipt.IdempotencyKey), AgentId="navigator", State="completed", Version=1, CanonicalHash=digest, CreatedAt=persistedNow, CompletedAt=persistedNow, ReceiptStatus="Succeeded", ReceiptPayload=JsonSerializer.Serialize(persistedReceipt) });
        try { await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return new(NavigatorStoreOutcome.Succeeded); }
        catch (DbUpdateException) { await tx.RollbackAsync(cancellationToken); db.ChangeTracker.Clear(); return new(NavigatorStoreOutcome.Conflict); }
    }

    public async ValueTask<NavigatorReceiptRecord?> GetReceiptAsync(string tenant, string principal, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!NavigatorPersistenceMapping.TryUuid(tenant,out var t) || !NavigatorPersistenceMapping.TryUuid(principal,out var p)) return null;
        var row=await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==t && x.PrincipalId==p && x.ClientTaskId==NavigatorPersistenceMapping.ReceiptClientTaskId(idempotencyKey), cancellationToken);
        var value=row?.ReceiptPayload is null ? null : ReadReceipt(row.ReceiptPayload);
        return value is { ExpiresAt: var exp } && exp > now ? value : null;
    }

    public async ValueTask<NavigatorStoreOutcome> AppendEventAsync(string id, string tenant, string principal, long expectedSequence, NavigatorEvent value, CancellationToken cancellationToken = default)
    {
        if (!Scope(id,tenant,principal,out var sid,out var t,out var p)) return NavigatorStoreOutcome.Conflict;
        if (!NavigatorEventTypes.IsSafe(value) || value.SessionId != id || value.Sequence != expectedSequence) return NavigatorStoreOutcome.Conflict;
        var exists=await db.Sessions.AsNoTracking().AnyAsync(x=>x.Id==sid && x.TenantId==t && x.OwnerId==p,cancellationToken);
        if (!exists) return NavigatorStoreOutcome.NotFound;
        var prev=await db.Events.AsNoTracking().Where(x=>x.SessionId==sid && x.TenantId==t && x.PrincipalId==p).Select(x=>(long?)x.Sequence).MaxAsync(cancellationToken) ?? -1;
        if (prev+1 != expectedSequence) return NavigatorStoreOutcome.Conflict;
        db.Events.Add(new EventEntity { Id=Guid.TryParse(value.EventId,out var eid)?eid:Guid.NewGuid(), SessionId=sid,TenantId=t,PrincipalId=p,Sequence=value.Sequence,EventType=value.Type,Payload=JsonSerializer.Serialize(value.Metadata ?? new Dictionary<string,string>()),OccurredAt=value.OccurredAt,CorrelationId=value.CorrelationId });
        try { await db.SaveChangesAsync(cancellationToken); return NavigatorStoreOutcome.Succeeded; } catch(DbUpdateException) { db.ChangeTracker.Clear(); return NavigatorStoreOutcome.Conflict; }
    }

    public async ValueTask<IReadOnlyList<NavigatorEvent>> GetEventsAsync(string id, string tenant, string principal, long afterSequence, CancellationToken cancellationToken = default)
    {
        if (!Scope(id,tenant,principal,out var sid,out var t,out var p)) return Array.Empty<NavigatorEvent>();
        var rows = await db.Events.AsNoTracking().Where(x=>x.SessionId==sid && x.TenantId==t && x.PrincipalId==p && x.Sequence>afterSequence).OrderBy(x=>x.Sequence).ToListAsync(cancellationToken);
        return rows.Select(x=>new NavigatorEvent(x.Id.ToString("D"),x.EventType,id,null,x.Sequence,x.CorrelationId,x.OccurredAt,JsonSerializer.Deserialize<Dictionary<string,string>>(x.Payload))).ToList();
    }

    public async ValueTask<bool> IsFenceCurrentAsync(string tenant, string executionId, long fencingToken, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!TryExecutionTenant(executionId, out var executionTenant) || !NavigatorPersistenceMapping.TryUuid(tenant,out var suppliedTenant) || executionTenant != suppliedTenant) return false;
        return await db.Leases.AsNoTracking().AnyAsync(x=>x.TenantId==executionTenant && x.Name==executionId && x.Version==fencingToken && x.ExpiresAt>now,cancellationToken);
    }

    public async ValueTask<OperationalLease?> TryAcquireLeaseAsync(string executionId,string owner,TimeSpan duration,DateTimeOffset now,CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || string.IsNullOrWhiteSpace(owner) || !TryExecutionTenant(executionId, out var tenant)) return null;
        var expiry = now + duration;
        // The insert-or-expired-row update is a single PostgreSQL statement. Version advances
        // only for the winner, so it is the monotonic fencing token across processes.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO oc_leases (tenant_id, name, owner, expires_at, version)
            VALUES ({tenant}, {executionId}, {owner}, {expiry}, 1)
            ON CONFLICT (tenant_id, name) DO UPDATE
            SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at, version = oc_leases.version + 1
            WHERE oc_leases.expires_at <= {now}
            """, cancellationToken);
        if (changed == 0) return null;
        var row = await db.Leases.AsNoTracking().SingleAsync(x => x.TenantId == tenant && x.Name == executionId && x.Owner == owner && x.ExpiresAt == expiry, cancellationToken);
        return new OperationalLease(executionId, executionId, row.Version, row.ExpiresAt);
    }

    public async ValueTask<OperationalLease?> RenewLeaseAsync(OperationalLease lease,TimeSpan duration,DateTimeOffset now,CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || !TryLeaseTenant(lease, out var tenant)) return null;
        var row = await db.Leases.SingleOrDefaultAsync(x => x.TenantId == tenant && x.Name == lease.ExecutionId, cancellationToken);
        if (row is null || row.Version != lease.FencingToken || row.ExpiresAt <= now) return null;
        var expiry = now + duration;
        row.ExpiresAt = expiry;
        row.Version = checked(row.Version + 1);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new OperationalLease(lease.LeaseId, lease.ExecutionId, row.Version, expiry);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
            return null;
        }
    }

    public async ValueTask<bool> IsLeaseCurrentAsync(OperationalLease lease,DateTimeOffset now,CancellationToken cancellationToken) =>
        TryLeaseTenant(lease, out var tenant) && await db.Leases.AsNoTracking().AnyAsync(x => x.TenantId == tenant && x.Name == lease.ExecutionId && x.Version == lease.FencingToken && x.ExpiresAt > now, cancellationToken);

    public async ValueTask ReleaseLeaseAsync(OperationalLease lease,CancellationToken cancellationToken)
    {
        if (!TryLeaseTenant(lease, out var tenant)) return;
        var row = await db.Leases.SingleOrDefaultAsync(x => x.TenantId == tenant && x.Name == lease.ExecutionId && x.Version == lease.FencingToken, cancellationToken);
        if (row is null) return;
        // Retain expired lease rows so reacquisition advances the version and fencing token.
        // Cleanup of long-expired rows is a separate, explicit follow-up.
        // Use the minimum representable instant because this API has no clock parameter;
        // this also expires the row for callers using a captured/injected clock value.
        row.ExpiresAt = DateTimeOffset.MinValue;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { db.Entry(row).State = EntityState.Detached; }
    }

    // Operational receipts are tenant-scoped by contract. PrincipalId therefore equals TenantId;
    // Navigator user receipts use IDurableNavigatorStore's full tenant/principal-scoped path.
    public async ValueTask<OperationalReceipt?> GetReceiptAsync(string tenantScope,string idempotencyKey,CancellationToken cancellationToken)
    {
        if (!NavigatorPersistenceMapping.TryUuid(tenantScope, out var tenant)) return null;
        var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.PrincipalId == tenant && x.ClientTaskId == idempotencyKey, cancellationToken);
        if (task?.ReceiptPayload is null) return null;
        try { return JsonSerializer.Deserialize<OperationalReceipt>(task.ReceiptPayload); } catch (JsonException) { return null; }
    }

    public async ValueTask<bool> TrySaveReceiptAsync(string tenantScope,string idempotencyKey,OperationalReceipt receipt,DateTimeOffset completedAt,CancellationToken cancellationToken)
    {
        if (!NavigatorPersistenceMapping.TryUuid(tenantScope, out var tenant) || receipt is null || receipt.Status != OperationalRunStatus.Completed || string.IsNullOrWhiteSpace(idempotencyKey)) return false;
        if (await db.Tasks.AsNoTracking().AnyAsync(x => x.TenantId == tenant && x.PrincipalId == tenant && x.ClientTaskId == idempotencyKey, cancellationToken)) return false;
        var id = NavigatorPersistenceMapping.ReceiptTaskId(tenant, tenant, idempotencyKey);
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant && x.OwnerId == tenant, cancellationToken);
        if (session is null)
        {
            db.Sessions.Add(new SessionEntity { Id = id, TenantId = tenant, OwnerId = tenant, Version = 1, CreatedAt = completedAt });
        }
        db.Tasks.Add(new TaskEntity { Id=id, TenantId=tenant, PrincipalId=tenant, SessionId=id, ClientTaskId=idempotencyKey, AgentId="navigator-operational", State="completed", Version=1, CanonicalHash="", CreatedAt=completedAt, CompletedAt=completedAt, ReceiptStatus="Succeeded", ReceiptPayload=JsonSerializer.Serialize(receipt) });
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    private static bool TryExecutionTenant(string executionId, out Guid tenant)
    {
        tenant = default;
        if (string.IsNullOrEmpty(executionId)) return false;
        var separator = executionId.IndexOf(':');
        return separator > 0 && separator < executionId.Length - 1 && NavigatorPersistenceMapping.TryUuid(executionId[..separator], out tenant);
    }

    private static bool TryLeaseTenant(OperationalLease lease, out Guid tenant) =>
        TryExecutionTenant(lease.ExecutionId, out tenant);

    private static bool Scope(string id,string tenant,string principal,out Guid sid,out Guid tid,out Guid pid)
    {
        var a=NavigatorPersistenceMapping.TryUuid(id,out sid); var b=NavigatorPersistenceMapping.TryUuid(tenant,out tid); var c=NavigatorPersistenceMapping.TryUuid(principal,out pid); return a&&b&&c;
    }
    private static TaskEntity SessionTask(Guid id,Guid tenant,Guid principal,DateTimeOffset now) => new() { Id=id,TenantId=tenant,PrincipalId=principal,SessionId=id,ClientTaskId=NavigatorPersistenceMapping.SessionClientTaskId(id),AgentId="navigator",State="active",Version=1,CanonicalHash="",CreatedAt=now };
    // PostgreSQL timestamptz stores microseconds; rounding down avoids extending expiry.
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, value.Offset);
    private static NavigatorReceiptRecord? ReadReceipt(string payload) { try { return JsonSerializer.Deserialize<NavigatorReceiptRecord>(payload); } catch(JsonException) { return null; } }
}
