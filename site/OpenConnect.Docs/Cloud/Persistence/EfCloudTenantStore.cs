using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudTenantStore(
    IDbContextFactory<CloudControlPlaneDbContext> contextFactory,
    TimeProvider timeProvider) : ICloudTenantStore
{
    private const string TenantOwner = "Owner";
    private static readonly string[] ApplicationAdministrators = ["Owner", "Administrator"];

    public async Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await (
            from membership in db.Memberships.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.SubjectIssuer == subject.Issuer && membership.SubjectId == subject.SubjectId
            orderby tenant.Name
            select new CloudTenantSummary(tenant.Id, tenant.Name, membership.Role, tenant.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var tenant = new CloudTenant { Id = Guid.NewGuid(), Name = name.Trim(), CreatedAt = now };
        db.Tenants.Add(tenant);
        db.Memberships.Add(new CloudMembership
        {
            TenantId = tenant.Id,
            SubjectIssuer = subject.Issuer,
            SubjectId = subject.SubjectId,
            Role = TenantOwner,
            CreatedAt = now,
        });
        db.AuditEvents.Add(CreateAudit(subject, tenant.Id, TenantOwner, "tenant.created", tenant.Id.ToString("D"), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CloudTenantSummary(tenant.Id, tenant.Name, TenantOwner, tenant.CreatedAt);
    }

    public async Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(
        CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var authorized = await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId &&
            x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId &&
            (x.Role == "Owner" || x.Role == "Administrator"), cancellationToken);
        if (!authorized) return null;
        return await db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId &&
                db.Memberships.Any(actor => actor.TenantId == tenantId && actor.SubjectIssuer == subject.Issuer &&
                    actor.SubjectId == subject.SubjectId && (actor.Role == "Owner" || actor.Role == "Administrator")))
            .OrderBy(x => x.SubjectIssuer).ThenBy(x => x.SubjectId)
            .Select(x => new CloudMemberSummary(x.SubjectIssuer, x.SubjectId, x.Role, x.CreatedAt, x.Revision))
            .ToListAsync(cancellationToken);
    }

    public async Task<CloudMemberChangeResult> ChangeMemberAsync(
        CloudSubject actor, Guid tenantId, CloudSubject target, string? role, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var actorRole = await db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId &&
            x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId).Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        if (actorRole is null) return new("not_found");
        if (actorRole != TenantOwner) return new("forbidden");
        var member = await db.Memberships.SingleOrDefaultAsync(x => x.TenantId == tenantId &&
            x.SubjectIssuer == target.Issuer && x.SubjectId == target.SubjectId, cancellationToken);
        if (member is null) return new("not_found");
        if (member.Revision != expectedRevision) return new("conflict", member.Revision);
        if (member.Role == TenantOwner && role != TenantOwner &&
            await db.Memberships.CountAsync(x => x.TenantId == tenantId && x.Role == TenantOwner, cancellationToken) <= 1)
            return new("last_owner");

        var revision = member.Revision + 1;
        var previousRole = member.Role;
        if (role is null) db.Memberships.Remove(member);
        else { member.Role = role; member.Revision = revision; }
        var targetHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{target.Issuer.Length}:{target.Issuer}{target.SubjectId}")));
        var action = role is null ? $"membership.removed.{previousRole}" : $"membership.role_changed.{previousRole}.{role}";
        db.AuditEvents.Add(CreateAudit(actor, tenantId, actorRole, action, targetHash, timeProvider.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("succeeded", role is null ? null : revision);
        }
        catch (DbUpdateConcurrencyException) { return new("conflict"); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }) { return new("conflict"); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { return new("conflict"); }
    }

    public async Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(
        CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(db, subject, tenantId, cancellationToken))
        {
            return null;
        }

        return await db.Applications.AsNoTracking()
            .Where(application => application.TenantId == tenantId)
            .OrderBy(application => application.Name)
            .Select(application => new CloudApplicationSummary(
                application.Id, application.TenantId, application.Name, application.Environment, application.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(
        CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var role = await db.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenantId &&
                membership.SubjectIssuer == subject.Issuer && membership.SubjectId == subject.SubjectId)
            .Select(membership => membership.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            return (null, true);
        }
        if (!ApplicationAdministrators.Contains(role, StringComparer.Ordinal))
        {
            return (null, true);
        }

        var now = timeProvider.GetUtcNow();
        var application = new CloudApplication
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = name.Trim(), Environment = environment, CreatedAt = now,
        };
        db.Applications.Add(application);
        db.AuditEvents.Add(CreateAudit(subject, tenantId, role, "application.created", application.Id.ToString("D"), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (new CloudApplicationSummary(application.Id, tenantId, application.Name, application.Environment, now), false);
    }

    private static Task<bool> IsMemberAsync(
        CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
        db.Memberships.AsNoTracking().AnyAsync(membership => membership.TenantId == tenantId &&
            membership.SubjectIssuer == subject.Issuer && membership.SubjectId == subject.SubjectId, cancellationToken);

    private static CloudAuditEvent CreateAudit(
        CloudSubject subject, Guid tenantId, string role, string action, string resourceId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = subject.Issuer, SubjectId = subject.SubjectId,
        ActorRole = role, Action = action, ResourceId = resourceId, Outcome = "succeeded", OccurredAt = now,
    };
}
