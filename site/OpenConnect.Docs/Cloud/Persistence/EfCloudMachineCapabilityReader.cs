using System.Data;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudMachineCapabilityReader(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudMachineCapabilityReader
{
    public async Task<CloudCapabilitySnapshot?> GetCurrentAsync(CloudCredentialIdentity identity, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var active = await db.Credentials.AsNoTracking().AnyAsync(x => x.Id == identity.CredentialId &&
            x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId && x.Environment == identity.Environment &&
            x.RevokedAt == null && x.ExpiresAt != null && x.ExpiresAt > now &&
            db.Applications.Any(app => app.TenantId == x.TenantId && app.Id == x.ApplicationId && app.Environment == x.Environment), cancellationToken);
        if (!active)
            return null;
        var snapshot = await EfCloudCapabilityReader.LoadDataAsync(db, identity.TenantId, identity.ApplicationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }
}
