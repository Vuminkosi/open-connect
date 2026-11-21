using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInvitationStore(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : ICloudInvitationStore
{
    public async Task<IReadOnlyList<CloudInvitationSummary>?> ListAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId && x.Role == "Owner", cancellationToken)) return null;
        return await db.Invitations.AsNoTracking().Where(x => x.TenantId == tenantId &&
                db.Memberships.Any(member => member.TenantId == tenantId && member.SubjectIssuer == actor.Issuer && member.SubjectId == actor.SubjectId && member.Role == "Owner"))
            .OrderByDescending(x => x.ExpiresAt)
            .Select(x => new CloudInvitationSummary(x.Id, x.SubjectId, x.Role, x.ExpiresAt, x.AcceptedAt, x.RevokedAt, x.Revision))
            .ToListAsync(cancellationToken);
    }

    public async Task<CloudInvitationChangeResult> RevokeAsync(CloudSubject actor, Guid tenantId, Guid invitationId, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var role = await db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId)
            .Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        if (role is null) return await RevokeFailureAsync("not_found", null, null);
        if (role != "Owner") return await RevokeFailureAsync("forbidden", role, null);
        var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invitationId, cancellationToken);
        if (invitation is null) return await RevokeFailureAsync("not_found", role, null);
        if (invitation.Revision != expectedRevision || invitation.Revision == int.MaxValue) return await RevokeFailureAsync("conflict", role, invitation.Revision);
        if (invitation.AcceptedAt is not null || invitation.RevokedAt is not null) return await RevokeFailureAsync("invitation_closed", role, invitation.Revision);
        var now = timeProvider.GetUtcNow();
        invitation.RevokedAt = now;
        // Older control-plane instances validate the digest but do not know RevokedAt.
        invitation.SecretHash = RandomNumberGenerator.GetBytes(32);
        invitation.Revision++;
        db.AuditEvents.Add(Audit(actor, tenantId, role, "invitation.revoked", invitationId, now));
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return new("succeeded", invitation.Revision); }
        catch (DbUpdateConcurrencyException) { return await RevokeFailureAsync("conflict", role, null); }
        catch (Exception exception) when (IsConflict(exception)) { return await RevokeFailureAsync("conflict", role, null); }

        async Task<CloudInvitationChangeResult> RevokeFailureAsync(string outcome, string? actorRole, int? revision)
        {
            await AuditFailureAsync(actor, tenantId, actorRole, "invitation.revoke", invitationId, outcome, cancellationToken);
            return new(outcome, revision);
        }
    }

    public async Task<string> CreateAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string role, Guid id, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var actorRole = await db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId)
            .Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
        if (actorRole is null) return await CreateFailureAsync("not_found", null);
        if (actorRole != "Owner") return await CreateFailureAsync("forbidden", actorRole);
        if (await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SubjectIssuer == target.Issuer && x.SubjectId == target.SubjectId, cancellationToken))
            return await CreateFailureAsync("already_member", actorRole);
        if (await HasPriorRemovalAsync(db, tenantId, target, cancellationToken)) return await CreateFailureAsync("reinvitation_unavailable", actorRole);
        db.Invitations.Add(new CloudInvitation { Id = id, TenantId = tenantId, SubjectIssuer = target.Issuer, SubjectId = target.SubjectId,
            Role = role, SecretHash = secretHash, ExpiresAt = expiresAt });
        db.AuditEvents.Add(Audit(actor, tenantId, actorRole, "invitation.created", id, timeProvider.GetUtcNow()));
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return "succeeded"; }
        catch (Exception exception) when (IsConflict(exception)) { return await CreateFailureAsync("conflict", actorRole); }

        async Task<string> CreateFailureAsync(string outcome, string? authenticatedRole)
        {
            await AuditFailureAsync(actor, tenantId, authenticatedRole, "invitation.create", null, outcome, cancellationToken);
            return outcome;
        }
    }

    public async Task<string> AcceptAsync(CloudSubject actor, Guid id, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (invitation is null || !CryptographicOperations.FixedTimeEquals(invitation.SecretHash, secretHash) ||
            invitation.SubjectIssuer != actor.Issuer || invitation.SubjectId != actor.SubjectId ||
            invitation.AcceptedAt is not null || invitation.RevokedAt is not null || invitation.Revision == int.MaxValue || invitation.ExpiresAt <= now) return "invalid_invitation";
        if (await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == invitation.TenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId, cancellationToken))
            return "invalid_invitation";

        // Reinstatement remains disabled until legacy removals can be reconciled safely.
        if (await HasPriorRemovalAsync(db, invitation.TenantId, actor, cancellationToken))
            return "reinvitation_unavailable";

        db.Memberships.Add(new CloudMembership { TenantId = invitation.TenantId, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
            Role = invitation.Role, Revision = 1, CreatedAt = now });
        invitation.AcceptedAt = now;
        invitation.Revision++;
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

    private async Task AuditFailureAsync(CloudSubject actor, Guid tenantId, string? role, string action, Guid? invitationId,
        string outcome, CancellationToken cancellationToken)
    {
        // A separate context survives an aborted serializable transaction. Unaffiliated actors
        // cannot add entries to a tenant's audit trail, and invitation secrets are never recorded.
        await using var auditDb = await contextFactory.CreateDbContextAsync(cancellationToken);
        auditDb.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = role is null ? null : tenantId,
            SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId, ActorRole = role ?? "AuthenticatedUser",
            Action = action, ResourceId = role is null ? tenantId.ToString("D") : invitationId?.ToString("D"),
            Outcome = outcome, OccurredAt = timeProvider.GetUtcNow(),
        });
        await auditDb.SaveChangesAsync(cancellationToken);
    }

    private static CloudAuditEvent Audit(CloudSubject actor, Guid tenantId, string role, string action, Guid id, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
        ActorRole = role, Action = action, ResourceId = id.ToString("D"), Outcome = "succeeded", OccurredAt = now,
    };
}
