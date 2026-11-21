using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudKnowledgeEmbeddingSettlementPostgresTests
{
    [CloudPostgresFact]
    public async Task SettlementRequiresBothPrivacyDecisionsAndRecordsBoundCharacterUsageOnce()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Embedding settlement", default);
        var application = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var bucket = Guid.NewGuid(); var reservation = Guid.NewGuid(); var claim = Guid.NewGuid();
        var deployment = Guid.NewGuid(); var request = Guid.NewGuid(); var fingerprint = new byte[32];
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucket, TenantId = tenant.Id, ApplicationId = application.Id,
                CapabilityId = "knowledge.embed", Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10, Reserved = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deployment, CapabilityId = "knowledge.embed",
                EndpointReference = "embedding-" + deployment.ToString("N"), Transport = "https-mtls", Region = "region-a",
                ContractVersion = "openconnect.v1", ImageDigest = "sha256:" + new string('a', 64), Enabled = true, ConcurrencyLimit = 1 });
            await db.SaveChangesAsync();
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = reservation, BucketId = bucket, TenantId = tenant.Id,
                ApplicationId = application.Id, RequestId = request, Amount = 1, ExpiresAt = now.AddMinutes(1),
                FingerprintKeyId = "active", RequestFingerprint = fingerprint });
            await db.SaveChangesAsync();
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = reservation, ClaimId = claim, TenantId = tenant.Id,
                ApplicationId = application.Id, DeploymentId = deployment, Region = "region-a", WorkerPayloadFingerprint = new byte[32], ClaimedAt = now });
            await db.SaveChangesAsync();
            db.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = reservation, ClaimId = claim, DeploymentId = deployment, ConsumedAt = now });
            await db.SaveChangesAsync();
        }
        var identity = new CloudCredentialIdentity(Guid.NewGuid(), tenant.Id, application.Id, "Development", new HashSet<string>());
        var evidence = new EfCloudKnowledgeEmbeddingPrivacyEvidenceStore(factory, TimeProvider.System);
        await evidence.AppendAsync(identity, request, "active", fingerprint, "request", "allowed", new string('a', 32), [], 12, default);
        var completion = new EfCloudKnowledgeEmbeddingCompletionStore(factory, TimeProvider.System);
        Assert.Equal("unavailable", (await completion.CompleteAsync(tenant.Id, application.Id, reservation, claim, default)).Status);
        await evidence.AppendAsync(identity, request, "active", fingerprint, "response", "allowed", new string('b', 32), [], null, default);
        Assert.Equal("unavailable", (await completion.CompleteAsync(Guid.NewGuid(), application.Id, reservation, claim, default)).Status);
        Assert.Equal("unavailable", (await completion.CompleteAsync(tenant.Id, application.Id, reservation, Guid.NewGuid(), default)).Status);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            completion.CompleteAsync(tenant.Id, application.Id, reservation, claim, default)));
        Assert.Single(attempts, result => result.Status == "completed");
        Assert.Equal(7, attempts.Count(result => result.Status == "already_completed"));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var characters = await db.Database.SqlQuery<int>($"SELECT \"InputCharacters\" AS \"Value\" FROM cloud.knowledge_embedding_usage WHERE \"ReservationId\" = {reservation}").SingleAsync();
            Assert.Equal(12, characters);
            var state = await db.QuotaReservations.SingleAsync(x => x.Id == reservation);
            Assert.Equal(1, state.Actual);
            Assert.NotNull(state.FinalizedAt);
            var quota = await db.QuotaBuckets.SingleAsync(x => x.Id == bucket);
            Assert.Equal(0, quota.Reserved);
            Assert.Equal(1, quota.Consumed);
        }
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
