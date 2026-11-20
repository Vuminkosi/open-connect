using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInvitationStore(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : ICloudInvitationStore
{
    public async Task<string> CreateAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string role, Guid id, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var actorRole = await db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId)
            .Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        if (actorRole is null) return "not_found";
        if (actorRole != "Owner") return "forbidden";
        if (await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SubjectIssuer == target.Issuer && x.SubjectId == target.SubjectId, cancellationToken))
            return "already_member";
        if (await HasPriorRemovalAsync(db, tenantId, target, cancellationToken)) return "reinvitation_unavailable";
        db.Invitations.Add(new CloudInvitation { Id = id, TenantId = tenantId, SubjectIssuer = target.Issuer, SubjectId = target.SubjectId,
            Role = role, SecretHash = secretHash, ExpiresAt = expiresAt });
        db.AuditEvents.Add(Audit(actor, tenantId, actorRole, "invitation.created", id, timeProvider.GetUtcNow()));
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return "succeeded"; }
        catch (Exception exception) when (IsConflict(exception)) { return "conflict"; }
    }

    public async Task<string> AcceptAsync(CloudSubject actor, Guid id, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (invitation is null || !CryptographicOperations.FixedTimeEquals(invitation.SecretHash, secretHash) ||
            invitation.SubjectIssuer != actor.Issuer || invitation.SubjectId != actor.SubjectId ||
            invitation.AcceptedAt is not null || invitation.ExpiresAt <= now) return "invalid_invitation";
        if (await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == invitation.TenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId, cancellationToken))
            return "invalid_invitation";

        // Reinstatement remains disabled until legacy removals can be reconciled safely.
        if (await HasPriorRemovalAsync(db, invitation.TenantId, actor, cancellationToken))
            return "reinvitation_unavailable";

        db.Memberships.Add(new CloudMembership { TenantId = invitation.TenantId, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
            Role = invitation.Role, Revision = 1, CreatedAt = now });
        invitation.AcceptedAt = now;
        db.AuditEvents.Add(Audit(actor, invitation.TenantId, invitation.Role, "invitation.accepted", invitation.Id, now));
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return "succeeded"; }
        catch (DbUpdateConcurrencyException) { return "invalid_invitation"; }
        catch (Exception exception) when (IsConflict(exception)) { return "invalid_invitation"; }
    }

    private static async Task<bool> HasPriorRemovalAsync(CloudControlPlaneDbContext db, Guid tenantId, CloudSubject subject, CancellationToken cancellationToken)
    {
        var subjectHash = CloudIdentityFingerprint.Compute(subject);
        return await db.MembershipRevisions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SubjectHash == subjectHash, cancellationToken) ||
            await db.AuditEvents.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ResourceId == subjectHash && x.Action.StartsWith("membership.removed"), cancellationToken);
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure }) return true;
        return false;
    }

    private static CloudAuditEvent Audit(CloudSubject actor, Guid tenantId, string role, string action, Guid id, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
        ActorRole = role, Action = action, ResourceId = id.ToString("D"), Outcome = "succeeded", OccurredAt = now,
    };
}
