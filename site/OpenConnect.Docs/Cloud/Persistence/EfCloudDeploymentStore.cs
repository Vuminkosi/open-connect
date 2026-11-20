using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudDeploymentStore(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : ICloudDeploymentStore
{
    public async Task<IReadOnlyList<CloudWorkerDeployment>> ListAsync(string capabilityId, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkerDeployments.AsNoTracking().Where(x => x.CapabilityId == capabilityId).OrderBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<string> PutAsync(CloudSubject actor, string traceId, CloudWorkerDeployment deployment, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.WorkerDeployments.SingleOrDefaultAsync(x => x.Id == deployment.Id, cancellationToken);
        if (expectedRevision == 0)
        {
            if (current is not null) { await RecordAsync(actor, traceId, deployment.Id, "conflict", cancellationToken); return "conflict"; }
            deployment.Revision = 1;
            db.WorkerDeployments.Add(deployment);
        }
        else
        {
            if (current is null || current.Revision != expectedRevision) { await RecordAsync(actor, traceId, deployment.Id, "conflict", cancellationToken); return "conflict"; }
            db.Entry(current).CurrentValues.SetValues(deployment);
            current.Revision = expectedRevision + 1;
        }
        db.AuditEvents.Add(CreateAudit(actor, traceId, deployment.Id, "succeeded"));
        try { await db.SaveChangesAsync(cancellationToken); return "succeeded"; }
        catch (DbUpdateConcurrencyException) { await RecordAsync(actor, traceId, deployment.Id, "conflict", cancellationToken); return "conflict"; }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { await RecordAsync(actor, traceId, deployment.Id, "conflict", cancellationToken); return "conflict"; }
        catch (DbUpdateException)
        {
            await RecordAsync(actor, traceId, deployment.Id, "failed", cancellationToken);
            throw;
        }
    }

    public async Task RecordAsync(CloudSubject actor, string traceId, Guid deploymentId, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(CreateAudit(actor, traceId, deploymentId, outcome));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordDeniedAsync(CloudSubject actor, string traceId, Guid deploymentId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var audit = CreateAudit(actor, traceId, deploymentId, "forbidden");
        audit.ActorRole = "AuthenticatedUser";
        db.AuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
    }

    private CloudAuditEvent CreateAudit(CloudSubject actor, string traceId, Guid deploymentId, string outcome) => new()
    {
        Id = Guid.NewGuid(), SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId, ActorRole = "PlatformOperator",
        Action = "deployment.write", ResourceId = deploymentId.ToString("D"), Outcome = outcome,
        TraceId = traceId, OccurredAt = timeProvider.GetUtcNow(),
    };
}
