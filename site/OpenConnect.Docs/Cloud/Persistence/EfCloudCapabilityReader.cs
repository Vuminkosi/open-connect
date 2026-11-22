using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudCapabilityReader(IDbContextFactory<CloudControlPlaneDbContext> contextFactory) : ICloudCapabilityReader
{
    public async Task<IReadOnlyList<CloudCapabilityDefinition>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Capabilities.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
    }

    public async Task<CloudCapabilitySnapshot?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await LoadAsync(db, subject, tenantId, null, cancellationToken);
    }

    public async Task<CloudCapabilitySnapshot?> GetApplicationAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await LoadAsync(db, subject, tenantId, applicationId, cancellationToken);
    }

    internal static async Task<CloudCapabilitySnapshot?> LoadAsync(
        CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, Guid? applicationId, CancellationToken cancellationToken)
    {
        if (!await db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId &&
                x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId, cancellationToken))
            return null;
        if (applicationId is not null && !await db.Applications.AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.Id == applicationId.Value, cancellationToken))
            return null;
        return await LoadDataAsync(db, tenantId, applicationId, cancellationToken);
    }

    internal static async Task<CloudCapabilitySnapshot> LoadDataAsync(
        CloudControlPlaneDbContext db, Guid tenantId, Guid? applicationId, CancellationToken cancellationToken)
    {
        var capabilities = await db.Capabilities.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var entitlements = await db.TenantEntitlements.AsNoTracking().Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        var tenantSelections = await db.TenantCapabilitySelections.AsNoTracking().Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        var applicationSelections = applicationId is null ? null : await db.ApplicationCapabilitySelections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId.Value)
            .ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        return new(capabilities, entitlements, tenantSelections, applicationSelections);
    }
}
