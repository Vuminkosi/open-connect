using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudCapabilitySelectionStore(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider)
    : ICloudCapabilitySelectionStore
{
    public async Task<CloudCapabilitySelectionResult> ChangeAsync(
        CloudSubject actor, Guid tenantId, Guid? applicationId, string capabilityId, bool enabled,
        Func<string, bool> authorize, Func<CloudCapabilitySnapshot, CloudCapabilitySelectionResult> evaluate,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var role = await db.Memberships.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SubjectIssuer == actor.Issuer && x.SubjectId == actor.SubjectId)
                .Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);
            if (role is null) return new("not_found");
            if (!authorize(role)) return new("forbidden");

            var snapshot = await EfCloudCapabilityReader.LoadAsync(db, actor, tenantId, applicationId, cancellationToken);
            if (snapshot is null) return new("not_found");
            var decision = evaluate(snapshot);
            if (decision.Status != "succeeded") return decision;

            if (applicationId is null)
            {
                var selection = await db.TenantCapabilitySelections.SingleOrDefaultAsync(
                    x => x.TenantId == tenantId && x.CapabilityId == capabilityId, cancellationToken);
                if (selection is null)
                    db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection
                    { TenantId = tenantId, CapabilityId = capabilityId, Enabled = enabled, Revision = decision.Revision!.Value });
                else { selection.Enabled = enabled; selection.Revision = decision.Revision!.Value; }
            }
            else
            {
                var selection = await db.ApplicationCapabilitySelections.SingleOrDefaultAsync(
                    x => x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == capabilityId, cancellationToken);
                if (selection is null)
                    db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection
                    { TenantId = tenantId, ApplicationId = applicationId.Value, CapabilityId = capabilityId, Enabled = enabled, Revision = decision.Revision!.Value });
                else { selection.Enabled = enabled; selection.Revision = decision.Revision!.Value; }
            }
            db.AuditEvents.Add(new CloudAuditEvent
            {
                Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
                ActorRole = role, Action = applicationId is null ? "tenant.capability.selection_changed" : "application.capability.selection_changed",
                ResourceId = applicationId?.ToString("D") ?? tenantId.ToString("D"), Outcome = "succeeded", OccurredAt = timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return decision;
        }
        catch (DbUpdateConcurrencyException) { return new("conflict"); }
        catch (Exception exception) when (IsConflict(exception)) { return new("conflict"); }
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres &&
                postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure)
                return true;
        return false;
    }
}
