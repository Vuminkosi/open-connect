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
        string availability;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.chat");
            availability = capability.PlatformAvailability;
            capability.PlatformAvailability = "available";
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "inference.chat", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "inference.chat", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.chat", Enabled = true, Revision = 1 });
            db.Credentials.Add(new CloudCredential { Id = credential, TenantId = tenant.Id, ApplicationId = app.Id, Environment = "Development", KeyHash = new byte[32], Fingerprint = "inference-claim", Scopes = "[\"inference.chat\"]", IssuedAt = now, ExpiresAt = now.AddHours(1) });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.chat", Unit = "request", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(10), Limit = 10 });
            await db.SaveChangesAsync();
        }
        try
        {
            var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
            var claims = new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, TimeProvider.System));
            var policies = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
            async Task<Guid> Reserve()
            {
                var reservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "inference.chat", "request", Guid.NewGuid(), "key", new byte[32], 1, now.AddMinutes(5)), default);
                Assert.Equal("reserved", reservation.Status);
                return reservation.ReservationId!.Value;
            }
            Task<CloudExecutionClaimResult> Claim(Guid reservation, string? model) => claims.ClaimAsync(credential, tenant.Id, app.Id, reservation, "inference.chat", "key", new byte[32], null, default, inferenceModelId: model);
            var id = await Reserve();
            Assert.Equal("invalid_request", (await Claim(id, null)).Status);
            Assert.Equal("invalid_request", (await Claim(id, "bad model")).Status);
            Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 0, default)).Status);
            Assert.Equal("unavailable", (await Claim(id, "registry/model-b")).Status);
            Assert.Equal("claimed", (await Claim(id, "registry/model-a")).Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
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
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 2, default)).Status);
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
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.chat");
            capability.PlatformAvailability = availability;
            await db.SaveChangesAsync();
        }
    }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
