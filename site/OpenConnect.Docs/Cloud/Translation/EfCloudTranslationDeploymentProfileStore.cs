using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudTranslationDeploymentProfileStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider clock)
    : ICloudTranslationDeploymentProfileStore
{
    private sealed class ProfileRow
    {
        public long DeploymentRevision { get; set; }
        public long Revision { get; set; }
        public required string TokenizerHash { get; set; }
    }

    private sealed class PairRow
    {
        public required string SourceLang { get; set; }
        public required string TargetLang { get; set; }
    }

    public async Task<CloudTranslationDeploymentProfile?> GetAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var profile = await db.Database.SqlQuery<ProfileRow>($"""
            SELECT "DeploymentRevision", "Revision", "TokenizerHash" FROM cloud.translation_deployment_profiles WHERE "DeploymentId" = {deploymentId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (profile is null) return null;
        var pairs = await db.Database.SqlQuery<PairRow>($"""
            SELECT "SourceLang", "TargetLang" FROM cloud.translation_deployment_pairs WHERE "DeploymentId" = {deploymentId}
            ORDER BY "SourceLang", "TargetLang"
            """).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(deploymentId, profile.DeploymentRevision, profile.Revision, profile.TokenizerHash,
            pairs.Select(x => new CloudTranslationLanguagePair(x.SourceLang, x.TargetLang)).ToArray());
    }

    public async Task<string> PutAsync(CloudSubject actor, string traceId, CloudTranslationDeploymentProfile profile, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {profile.DeploymentId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is null || deployment.CapabilityId != "translation.translate" || deployment.Revision != profile.DeploymentRevision)
            return "conflict";
        var current = await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM cloud.translation_deployment_profiles WHERE \"DeploymentId\" = {profile.DeploymentId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (current != expectedRevision) return "conflict";
        var next = expectedRevision + 1;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.translation_deployment_profiles ("DeploymentId", "DeploymentRevision", "Revision", "TokenizerHash")
            VALUES ({profile.DeploymentId}, {profile.DeploymentRevision}, {next}, {profile.TokenizerHash})
            ON CONFLICT ("DeploymentId") DO UPDATE SET "DeploymentRevision" = EXCLUDED."DeploymentRevision",
                "Revision" = EXCLUDED."Revision", "TokenizerHash" = EXCLUDED."TokenizerHash"
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.translation_deployment_pairs WHERE \"DeploymentId\" = {profile.DeploymentId}", cancellationToken);
        foreach (var pair in profile.LanguagePairs)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.translation_deployment_pairs ("DeploymentId", "SourceLang", "TargetLang")
                VALUES ({profile.DeploymentId}, {pair.SourceLang}, {pair.TargetLang})
                """, cancellationToken);
        db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), SubjectIssuer = actor.Issuer, SubjectId = actor.SubjectId,
            ActorRole = "PlatformOperator", Action = "translation.deployment.profile.write", ResourceId = profile.DeploymentId.ToString("D"),
            Outcome = "succeeded", TraceId = traceId, OccurredAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return "succeeded";
    }
}
