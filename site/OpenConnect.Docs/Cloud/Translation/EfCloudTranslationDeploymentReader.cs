using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudTranslationDeploymentReader(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudTranslationDeploymentReader
{
    public async Task<bool> MatchesAsync(Guid tenantId, CloudTranslationDeploymentMatchRequest request, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var policy = await db.RegionPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        var catalog = await db.Capabilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "translation.translate", cancellationToken);
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(request.Region, StringComparer.Ordinal)) return false;
        var worker = await db.WorkerDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DeploymentId && x.Enabled &&
            x.CapabilityId == "translation.translate" && x.Transport == "https-mtls" && x.EndpointReference == request.EndpointReference &&
            x.Region == request.Region && x.ContractVersion == request.ContractVersion && x.ModelId == request.ModelId &&
            x.ModelRevision == request.ModelRevision && x.ModelDigest == request.ModelDigest && x.ImageDigest == request.ImageDigest, cancellationToken);
        if (worker is null) return false;
        var matched = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM cloud.translation_deployment_profiles p
                JOIN cloud.translation_deployment_pairs l ON l."DeploymentId" = p."DeploymentId"
                WHERE p."DeploymentId" = {worker.Id} AND p."DeploymentRevision" = {worker.Revision}
                    AND p."TokenizerHash" = {request.TokenizerHash} AND l."SourceLang" = {request.SourceLang}
                    AND l."TargetLang" = {request.TargetLang}) AS "Value"
            """).SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return matched;
    }
}
