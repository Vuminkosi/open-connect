using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudInferenceClaimPostgresTests
{
    [CloudPostgresFact]
    public async Task InferenceClaimsRequireLockedTenantModelPolicy()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Inference claim", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var credential = Guid.NewGuid();
        string availability, allowedRegions; bool regionsVerified;
        var deploymentId = Guid.NewGuid();
        var deploymentRef = "inference-" + Guid.NewGuid().ToString("N");
        var match = new CloudInferenceDeploymentMatch(deploymentRef, "region-a", "openconnect.v1", "revision-1", "sha256:" + new string('a', 64));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.chat");
            availability = capability.PlatformAvailability; allowedRegions = capability.AllowedRegions; regionsVerified = capability.AllowedRegionsVerified;
            capability.PlatformAvailability = "available"; capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "inference.chat", EndpointReference = deploymentRef,
                Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = "registry/model-a", ModelRevision = "revision-1",
                ModelDigest = "sha256:" + new string('a', 64), ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 2 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "inference.chat", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "inference.chat", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.chat", Enabled = true, Revision = 1 });
            db.Credentials.Add(new CloudCredential { Id = credential, TenantId = tenant.Id, ApplicationId = app.Id, Environment = "Development", KeyHash = new byte[32], Fingerprint = "inference-claim", Scopes = "[\"inference.chat\"]", IssuedAt = now, ExpiresAt = now.AddHours(1) });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.chat", Unit = "token", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(10), Limit = 100 });
            await db.SaveChangesAsync();
        }
        try
        {
            var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
            var claims = new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, TimeProvider.System));
            var policies = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
            async Task<Guid> Reserve()
            {
                var reservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "inference.chat", "token", Guid.NewGuid(), "key", new byte[32], 5, now.AddMinutes(5)), default);
                Assert.Equal("reserved", reservation.Status);
                return reservation.ReservationId!.Value;
            }
            Task<CloudExecutionClaimResult> Claim(Guid reservation, string? model) => claims.ClaimAsync(credential, tenant.Id, app.Id, reservation, "inference.chat", "key", new byte[32], null, default, workerPayloadFingerprint: new byte[32], inferenceModelId: model, inferenceDeployment: match);
            var id = await Reserve();
            Assert.Equal("invalid_request", (await claims.ClaimAsync(credential, tenant.Id, app.Id, id, "inference.chat", "key", new byte[32], null, default, inferenceModelId: "registry/model-a", inferenceDeployment: match)).Status);
            Assert.Equal("invalid_request", (await Claim(id, null)).Status);
            Assert.Equal("invalid_request", (await Claim(id, "bad model")).Status);
            Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 0, default)).Status);
            Assert.Equal("unavailable", (await Claim(id, "registry/model-b")).Status);
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[]"));
            Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-a\"]"));
            var mismatched = await claims.ClaimAsync(credential, tenant.Id, app.Id, id, "inference.chat", "key", new byte[32], null, default,
                workerPayloadFingerprint: new byte[32], inferenceModelId: "registry/model-a", inferenceDeployment: match with { ModelDigest = "sha256:" + new string('c', 64) });
            Assert.Equal("unavailable", mismatched.Status);
            var firstClaim = await Claim(id, "registry/model-a");
            Assert.Equal("claimed", firstClaim.Status);
            var consumption = new CloudInferenceConsumptionService(new EfCloudInferenceConsumptionStore(factory, TimeProvider.System));
            Assert.Equal("unavailable", await consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, Guid.NewGuid(), new byte[32], default));
            Assert.Equal("unavailable", await consumption.ConsumeAsync(id, firstClaim.ClaimId.Value, deploymentId, Enumerable.Repeat((byte)1, 32).ToArray(), default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(deploymentId, (await db.ExecutionClaims.SingleAsync(x => x.ReservationId == id)).DeploymentId);
                Assert.Equal("registry/model-a", await db.Database.SqlQuery<string>($"SELECT \"ModelId\" AS \"Value\" FROM cloud.execution_claim_inference_models WHERE \"ReservationId\" = {id}").SingleAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.execution_claim_inference_models WHERE \"ReservationId\" = {id}"));
            }
            var race = await Reserve();
            await using (var writer = await factory.CreateDbContextAsync())
            {
                await using var transaction = await writer.Database.BeginTransactionAsync();
                await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.inference_model_policies SET \"AllowedModels\" = '[]'::jsonb WHERE \"TenantId\" = {tenant.Id}");
                var pending = Claim(race, "registry/model-a");
                await Task.Delay(100);
                Assert.False(pending.IsCompleted);
                await transaction.CommitAsync();
                Assert.Equal("unavailable", (await pending).Status);
            }
            Assert.Equal("unavailable", await consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, deploymentId, new byte[32], default));
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 2, default)).Status);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, deploymentId, new byte[32], default)));
            Assert.Single(attempts, x => x == "consumed");
            Assert.Equal(9, attempts.Count(x => x == "already_consumed"));
            var claimFirst = await Reserve();
            await using (var reservationBlocker = await factory.CreateDbContextAsync())
            {
                await using var transaction = await reservationBlocker.Database.BeginTransactionAsync();
                await reservationBlocker.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {claimFirst} FOR UPDATE").AsNoTracking().SingleAsync();
                var pendingClaim = Claim(claimFirst, "registry/model-a");
                await Task.Delay(150);
                Assert.False(pendingClaim.IsCompleted);
                var pendingRemoval = policies.PutAsync(owner, tenant.Id, [], 3, default);
                await Task.Delay(150);
                Assert.False(pendingRemoval.IsCompleted);
                await transaction.CommitAsync();
                Assert.Equal("claimed", (await pendingClaim).Status);
                Assert.Equal("succeeded", (await pendingRemoval).Status);
            }
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 4, default)).Status);
            var atCapacity = await Reserve();
            Assert.Equal("unavailable", (await Claim(atCapacity, "registry/model-a")).Status);
            var settlement = new CloudInferenceTokenSettlement(new EfCloudInferenceTokenSettlementStore(factory, TimeProvider.System));
            Assert.Equal("invalid_request", await settlement.SettleAsync(id, firstClaim.ClaimId!.Value, deploymentId, new(-1, 0), default));
            Assert.Equal("invalid_request", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(long.MaxValue, 1), default));
            Assert.Equal("unavailable", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, Guid.NewGuid(), new(2, 1), default));
            Assert.Equal("overage_requires_reconciliation", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(3, 3), default));
            var completions = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(2, 1), default)));
            Assert.Single(completions, x => x == "completed");
            Assert.Equal(9, completions.Count(x => x == "already_completed"));
            Assert.Equal("conflict", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(1, 2), default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var saved = await db.QuotaReservations.SingleAsync(x => x.Id == id);
                Assert.Equal(3, saved.Actual);
                Assert.Equal(3, (await db.QuotaBuckets.SingleAsync(x => x.Id == saved.BucketId)).Consumed);
                Assert.Equal(3, (await db.UsageRecords.SingleAsync(x => x.ReservationId == id)).Quantity);
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.inference_token_observations SET \"InputTokens\" = 0 WHERE \"ReservationId\" = {id}"));
            }
            // A legacy full-charge completion cannot finalize inference without token evidence and consumption.
            var forged = await Claim(atCapacity, "registry/model-a");
            Assert.Equal("claimed", forged.Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = atCapacity, ClaimId = forged.ClaimId!.Value, Outcome = "completed", CompletedAt = now });
                await db.SaveChangesAsync();
                await Assert.ThrowsAnyAsync<Exception>(() => db.QuotaReservations.Where(x => x.Id == atCapacity)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 5L).SetProperty(y => y.FinalizedAt, DateTimeOffset.UtcNow)));
            }
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.chat");
            capability.PlatformAvailability = availability; capability.AllowedRegions = allowedRegions; capability.AllowedRegionsVerified = regionsVerified;
            await db.SaveChangesAsync();
        }
    }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
