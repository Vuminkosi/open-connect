using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudLiveUIDualSettlementPostgresTests
{
    [CloudPostgresFact]
    public async Task ReconciliationReaderPagesOnlyExpiredConsumedUsageWithoutObservations()
    {
        var unknown = new[] { await Fixture.CreateAsync(), await Fixture.CreateAsync(), await Fixture.CreateAsync() };
        var observed = await Fixture.CreateAsync(); var fresh = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(observed.Factory, TimeProvider.System));
        Assert.Equal("observed", await service.ObserveAsync(observed.ReservationId, observed.ClaimId, observed.DeploymentId, new(30, 20), default));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var db = await observed.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.quota_reservations SET "ExpiresAt" = {expiry}
                WHERE "Id" IN ({unknown[0].ReservationId}, {unknown[1].ReservationId}, {unknown[2].ReservationId}, {observed.ReservationId})
                """);
        var reader = new EfCloudLiveUIReconciliationReader(observed.Factory);
        CloudLiveUIReconciliationCursor? cursor = null;
        var all = new List<CloudLiveUIUnknownUsage>();
        for (var page = 0; page < 100; page++)
        {
            var rows = await reader.ReadUnknownAsync(cursor, 2, DateTimeOffset.UtcNow.AddMinutes(-5), default);
            if (rows.Count == 0) break;
            all.AddRange(rows);
            cursor = new(rows[^1].ExpiresAt, rows[^1].ReservationId);
        }
        foreach (var item in unknown) Assert.Single(all, x => x.ReservationId == item.ReservationId && x.ReservedTokens == 100);
        Assert.DoesNotContain(all, x => x.ReservationId == observed.ReservationId || x.ReservationId == fresh.ReservationId);
        Assert.Equal(all.Count, all.Select(x => x.ReservationId).Distinct().Count());
        Assert.Equal("failed", await service.FailAsync(observed.ReservationId, observed.ClaimId, observed.DeploymentId, new(30, 20), default));
    }

    [CloudPostgresFact]
    public async Task ObservedRecoveryBatchReconcilesExpiredUsageAndLeavesUnknownAndFreshWorkHeld()
    {
        var observed = await Fixture.CreateAsync(); var unknown = await Fixture.CreateAsync(); var fresh = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(observed.Factory, TimeProvider.System));
        var usage = new CloudLiveUITokenUsage(30, 20);
        Assert.Equal("observed", await service.ObserveAsync(observed.ReservationId, observed.ClaimId, observed.DeploymentId, usage, default));
        Assert.Equal("observed", await service.ObserveAsync(fresh.ReservationId, fresh.ClaimId, fresh.DeploymentId, usage, default));
        await using (var db = await observed.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.quota_reservations SET "ExpiresAt" = {DateTimeOffset.UtcNow.AddMinutes(-10)}
                WHERE "Id" IN ({observed.ReservationId}, {unknown.ReservationId})
                """);
        var recovery = new CloudLiveUIObservedRecovery(observed.Factory, service, TimeProvider.System);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => recovery.RecoverExpiredAsync(default)));
        Assert.Equal(1, outcomes.Sum());
        Assert.Equal(0, await recovery.RecoverExpiredAsync(default));
        await using var verify = await observed.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.QuotaReservations.SingleAsync(x => x.Id == observed.ReservationId)).Actual);
        Assert.Null((await verify.QuotaReservations.SingleAsync(x => x.Id == unknown.ReservationId)).FinalizedAt);
        Assert.Null((await verify.QuotaReservations.SingleAsync(x => x.Id == fresh.ReservationId)).FinalizedAt);
        Assert.Equal(50, (await verify.QuotaBuckets.SingleAsync(x => x.Id == observed.TokenBucketId)).Consumed);
        Assert.Equal(100, (await verify.QuotaBuckets.SingleAsync(x => x.Id == unknown.TokenBucketId)).Reserved);
    }

    [CloudPostgresFact]
    public async Task ObservedRecoveryRequiresExpiredEvidenceAndSettlesOnlyOnce()
    {
        var fixture = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(fixture.Factory, TimeProvider.System));
        var usage = new CloudLiveUITokenUsage(30, 20);
        Assert.Equal("unavailable", await service.RecoverAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        Assert.Equal("observed", await service.ObserveAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        Assert.Equal("unavailable", await service.RecoverAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_execution_failures ("PlanReservationId", "ClaimId", "DeploymentId", "FailedAt", "Source")
                VALUES ({fixture.ReservationId}, {fixture.ClaimId}, {fixture.DeploymentId}, {DateTimeOffset.UtcNow}, {"observed_recovery"})
                """));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.quota_reservations SET "ExpiresAt" = {DateTimeOffset.UtcNow.AddMinutes(-10)} WHERE "Id" = {fixture.ReservationId}
                """);
        }
        Assert.Equal("conflict", await service.RecoverAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(31, 19), default));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RecoverAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default)));
        Assert.Single(results, result => result == "failed"); Assert.Equal(7, results.Count(result => result == "already_failed"));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(50, (await verify.QuotaBuckets.SingleAsync(x => x.Id == fixture.TokenBucketId)).Consumed);
        Assert.Equal("observed_recovery", await verify.Database.SqlQuery<string>($"""
            SELECT "Source" AS "Value" FROM cloud.liveui_execution_failures WHERE "PlanReservationId" = {fixture.ReservationId}
            """).SingleAsync());
        Assert.False(await verify.ExecutionCompletions.AnyAsync(x => x.ReservationId == fixture.ReservationId));
    }

    [CloudPostgresFact]
    public async Task DurableUsageObservationIsBoundedIdempotentAndMatchesLaterSettlement()
    {
        var fixture = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(fixture.Factory, TimeProvider.System));
        var usage = new CloudLiveUITokenUsage(30, 20);
        Assert.Equal("unavailable", await service.ObserveAsync(fixture.ReservationId, fixture.ClaimId, Guid.NewGuid(), usage, default));
        Assert.Equal("overage_requires_reconciliation", await service.ObserveAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(90, 11), default));
        var observations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ObserveAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default)));
        Assert.Single(observations, x => x == "observed"); Assert.Equal(7, observations.Count(x => x == "already_observed"));
        Assert.Equal("conflict", await service.ObserveAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(31, 19), default));
        Assert.Equal("conflict", await service.FailAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(31, 19), default));
        await using (var before = await fixture.Factory.CreateDbContextAsync())
        {
            Assert.Null((await before.QuotaReservations.SingleAsync(x => x.Id == fixture.ReservationId)).FinalizedAt);
            Assert.Equal(100, (await before.QuotaBuckets.SingleAsync(x => x.Id == fixture.TokenBucketId)).Reserved);
        }
        Assert.Equal("failed", await service.FailAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        Assert.Equal("already_observed", await service.ObserveAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(50, (await verify.QuotaBuckets.SingleAsync(x => x.Id == fixture.TokenBucketId)).Consumed);
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_token_observations WHERE \"PlanReservationId\" = {fixture.ReservationId}").SingleAsync());
    }

    [CloudPostgresFact]
    public async Task KnownUsageFromFailedExecutionSettlesOnceWithoutSuccessfulOutputEvidence()
    {
        var fixture = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(fixture.Factory, TimeProvider.System));
        var usage = new CloudLiveUITokenUsage(30, 20);
        Assert.Equal("unavailable", await service.FailAsync(fixture.ReservationId, fixture.ClaimId, Guid.NewGuid(), usage, default));
        Assert.Equal("overage_requires_reconciliation", await service.FailAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(99, 2), default));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.FailAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default)));
        Assert.Single(results, result => result == "failed");
        Assert.Equal(11, results.Count(result => result == "already_failed"));
        Assert.Equal("conflict", await service.FailAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(31, 19), default));
        Assert.Equal("conflict", await service.SettleAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var plan = await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.PlanBucketId);
        var tokens = await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.TokenBucketId);
        Assert.Equal(0, plan.Reserved); Assert.Equal(1, plan.Consumed);
        Assert.Equal(0, tokens.Reserved); Assert.Equal(50, tokens.Consumed);
        Assert.False(await verify.ExecutionCompletions.AnyAsync(item => item.ReservationId == fixture.ReservationId));
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_execution_failures WHERE \"PlanReservationId\" = {fixture.ReservationId}").SingleAsync());
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cloud.liveui_execution_failures SET "FailedAt" = {DateTimeOffset.UtcNow} WHERE "PlanReservationId" = {fixture.ReservationId}
            """));
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.execution_completions ("ReservationId", "ClaimId", "Outcome", "CompletedAt")
            VALUES ({fixture.ReservationId}, {fixture.ClaimId}, {"completed"}, {DateTimeOffset.UtcNow})
            """));
    }

    [CloudPostgresFact]
    public async Task SuccessAndFailureReportsCannotBothSettleOneClaim()
    {
        var fixture = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(fixture.Factory, TimeProvider.System));
        var usage = new CloudLiveUITokenUsage(30, 20);
        var outcomes = await Task.WhenAll(service.SettleAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default),
            service.FailAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default));
        Assert.Single(outcomes, item => item is "completed" or "failed");
        Assert.Single(outcomes, item => item == "conflict");
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await db.QuotaBuckets.SingleAsync(item => item.Id == fixture.PlanBucketId)).Consumed);
        Assert.Equal(50, (await db.QuotaBuckets.SingleAsync(item => item.Id == fixture.TokenBucketId)).Consumed);
    }

    [CloudPostgresFact]
    public async Task ExpiredUnconsumedClaimsRecoverExactlyOnceWhileConsumedClaimsRemainHeld()
    {
        var idle = await Fixture.CreateAsync(consume: false);
        var consumed = await Fixture.CreateAsync();
        var racing = await Fixture.CreateAsync(consume: false);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await idle.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.quota_reservations SET "ExpiresAt" = {now.AddMinutes(-10)}
                WHERE "Id" IN ({idle.ReservationId}, {consumed.ReservationId})
                """);
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.execution_abandonments ("ReservationId", "ClaimId", "AbandonedAt", "Reason")
                VALUES ({consumed.ReservationId}, {consumed.ClaimId}, {now}, {"expired_unconsumed"})
                """));
        }
        var recovery = new EfCloudLiveUIUnconsumedRecoveryStore(idle.Factory, TimeProvider.System);
        await using (var worker = await racing.Factory.CreateDbContextAsync())
        await using (var transaction = await worker.Database.BeginTransactionAsync())
        {
            await worker.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_metered_consumption_authorizations ("PlanReservationId", "ClaimId", "DeploymentId", "ContextTokens", "AuthorizedAt")
                VALUES ({racing.ReservationId}, {racing.ClaimId}, {racing.DeploymentId}, {100}, {now})
                """);
            worker.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = racing.ReservationId, ClaimId = racing.ClaimId,
                DeploymentId = racing.DeploymentId, ConsumedAt = now });
            await worker.SaveChangesAsync();
            await worker.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.quota_reservations SET "ExpiresAt" = {now.AddMinutes(-10)} WHERE "Id" = {racing.ReservationId}
                """);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => recovery.ReleaseAsync(now.AddMinutes(-5), 100, default)));
            Assert.Equal(1, results.Sum());
            await transaction.CommitAsync();
        }
        Assert.Equal(0, await recovery.ReleaseAsync(now.AddMinutes(-5), 100, default));
        await using var verify = await idle.Factory.CreateDbContextAsync();
        Assert.Equal(0, (await verify.QuotaBuckets.SingleAsync(b => b.Id == idle.PlanBucketId)).Reserved);
        Assert.Equal(0, (await verify.QuotaBuckets.SingleAsync(b => b.Id == idle.TokenBucketId)).Reserved);
        Assert.Equal(0, (await verify.QuotaReservations.SingleAsync(r => r.Id == idle.ReservationId)).Actual);
        Assert.Equal(1, (await verify.QuotaBuckets.SingleAsync(b => b.Id == consumed.PlanBucketId)).Reserved);
        Assert.Equal(100, (await verify.QuotaBuckets.SingleAsync(b => b.Id == consumed.TokenBucketId)).Reserved);
        Assert.Null((await verify.QuotaReservations.SingleAsync(r => r.Id == consumed.ReservationId)).FinalizedAt);
        Assert.Null((await verify.QuotaReservations.SingleAsync(r => r.Id == racing.ReservationId)).FinalizedAt);
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.execution_abandonments WHERE \"ReservationId\" = {idle.ReservationId}").SingleAsync());
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.liveui_metered_consumption_authorizations ("PlanReservationId", "ClaimId", "DeploymentId", "ContextTokens", "AuthorizedAt")
            VALUES ({idle.ReservationId}, {idle.ClaimId}, {idle.DeploymentId}, {100}, {now})
            """));
    }

    [CloudPostgresFact]
    public async Task ConsumedPlanAndTokensSettleAtomicallyExactlyOnce()
    {
        var fixture = await Fixture.CreateAsync();
        var usage = new CloudLiveUITokenUsage(30, 20);
        var settlement = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(fixture.Factory, TimeProvider.System));
        Assert.Equal("unavailable", await settlement.SettleAsync(fixture.ReservationId, fixture.ClaimId, Guid.NewGuid(), usage, default));
        Assert.Equal("overage_requires_reconciliation", await settlement.SettleAsync(fixture.ReservationId, fixture.ClaimId,
            fixture.DeploymentId, new(90, 11), default));
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_token_observations ("PlanReservationId", "ClaimId", "DeploymentId", "InputTokens", "OutputTokens", "ObservedAt")
                VALUES ({fixture.ReservationId}, {Guid.NewGuid()}, {fixture.DeploymentId}, {30L}, {20L}, {DateTimeOffset.UtcNow})
                """));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.liveui_token_reservations SET "Actual" = {50L}, "FinalizedAt" = {DateTimeOffset.UtcNow}
                WHERE "PlanReservationId" = {fixture.ReservationId}
                """));
        }
        var legacy = new EfCloudLiveUISettlementStore(fixture.Factory, TimeProvider.System);
        await Assert.ThrowsAsync<PostgresException>(() => legacy.SettleAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => settlement.SettleAsync(
            fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, usage, default)));
        Assert.Single(results, item => item == "completed");
        Assert.Equal(11, results.Count(item => item == "already_completed"));
        Assert.Equal("conflict", await settlement.SettleAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(31, 19), default));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var plan = await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.PlanBucketId);
        var tokens = await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.TokenBucketId);
        Assert.Equal(0, plan.Reserved); Assert.Equal(1, plan.Consumed);
        Assert.Equal(0, tokens.Reserved); Assert.Equal(50, tokens.Consumed);
        var reservation = await verify.QuotaReservations.SingleAsync(item => item.Id == fixture.ReservationId);
        Assert.Equal(1, reservation.Actual); Assert.NotNull(reservation.FinalizedAt);
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_token_observations WHERE \"PlanReservationId\" = {fixture.ReservationId}").SingleAsync());
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cloud.liveui_token_observations SET "InputTokens" = {31L} WHERE "PlanReservationId" = {fixture.ReservationId}
            """));
    }

    [CloudPostgresFact]
    public async Task DeterministicPlansSettleWithoutChargingModelTokens()
    {
        var fixture = await Fixture.CreateAsync();
        var service = new CloudLiveUIDualSettlement(new EfCloudLiveUIDualSettlementStore(fixture.Factory, TimeProvider.System));
        Assert.Equal("completed", await service.SettleAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, new(0, 0), default));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var token = await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.TokenBucketId);
        Assert.Equal(0, token.Reserved); Assert.Equal(0, token.Consumed);
    }

    [CloudPostgresFact]
    public async Task LegacyPlanOnlySettlementStillWorksAfterTheAdditiveFence()
    {
        var fixture = await Fixture.CreateAsync(dual: false);
        var legacy = new EfCloudLiveUISettlementStore(fixture.Factory, TimeProvider.System);
        Assert.Equal("completed", await legacy.SettleAsync(fixture.ReservationId, fixture.ClaimId, fixture.DeploymentId, default));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.PlanBucketId)).Consumed);
        Assert.Equal(0, (await verify.QuotaBuckets.SingleAsync(item => item.Id == fixture.TokenBucketId)).Consumed);
    }

    [CloudPostgresFact]
    public async Task DualReservationRejectsOldConsumptionAndUnpairedOrOversizedAuthorization()
    {
        var fixture = await Fixture.CreateAsync(consume: false);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = fixture.ReservationId, ClaimId = fixture.ClaimId,
                DeploymentId = fixture.DeploymentId, ConsumedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_metered_consumption_authorizations ("PlanReservationId", "ClaimId", "DeploymentId", "ContextTokens", "AuthorizedAt")
                VALUES ({fixture.ReservationId}, {fixture.ClaimId}, {fixture.DeploymentId}, {101}, {DateTimeOffset.UtcNow})
                """));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_metered_consumption_authorizations ("PlanReservationId", "ClaimId", "DeploymentId", "ContextTokens", "AuthorizedAt")
                VALUES ({fixture.ReservationId}, {fixture.ClaimId}, {fixture.DeploymentId}, {100}, {DateTimeOffset.UtcNow})
                """));
        }
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.False(await verify.ExecutionConsumptions.AnyAsync(item => item.ReservationId == fixture.ReservationId));
        Assert.Equal(0, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_metered_consumption_authorizations WHERE \"PlanReservationId\" = {fixture.ReservationId}").SingleAsync());
    }

    private sealed record Fixture(IDbContextFactory<CloudControlPlaneDbContext> Factory, Guid ReservationId, Guid ClaimId,
        Guid DeploymentId, Guid PlanBucketId, Guid TokenBucketId)
    {
        public static async Task<Fixture> CreateAsync(bool dual = true, bool consume = true)
        {
            var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
                .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
            IDbContextFactory<CloudControlPlaneDbContext> factory = new DbFactory(options);
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
            var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
            var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
            var tenant = await tenants.CreateTenantAsync(owner, "LiveUI settlement", default);
            var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
            var now = DateTimeOffset.UtcNow; var planId = Guid.NewGuid(); var tokenId = Guid.NewGuid(); var deploymentId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.QuotaBuckets.AddRange(Bucket(planId, tenant.Id, app.Id, "plan", now), Bucket(tokenId, tenant.Id, app.Id, "token", now));
                db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "inference.liveui",
                    EndpointReference = "fixture-" + Guid.NewGuid().ToString("N"), Transport = "https-mtls", Region = "test",
                    ContractVersion = "v1", ModelId = "owner/model", ModelRevision = "revision", ModelDigest = "sha256:" + new string('a', 64),
                    ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
                await db.SaveChangesAsync();
            }
            CloudQuotaResult reserved;
            if (dual)
            {
                var quota = new CloudLiveUIDualQuotaService(new EfCloudLiveUIDualQuotaStore(factory, TimeProvider.System), TimeProvider.System);
                reserved = await quota.ReserveAsync(new(tenant.Id, app.Id, Guid.NewGuid(), "key", new byte[32], 100, now.AddMinutes(5)), default);
            }
            else
            {
                var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
                reserved = await quota.ReserveAsync(new(tenant.Id, app.Id, "inference.liveui", "plan", Guid.NewGuid(), "key", new byte[32], 1, now.AddMinutes(5)), default);
            }
            Assert.Equal("reserved", reserved.Status);
            var reservationId = reserved.ReservationId!.Value; var claimId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = reservationId, TenantId = tenant.Id, ApplicationId = app.Id,
                    ClaimId = claimId, DeploymentId = deploymentId, Region = "test", WorkerPayloadFingerprint = new byte[32], ClaimedAt = now });
                await db.SaveChangesAsync();
                if (consume)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    if (dual)
                        await db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO cloud.liveui_metered_consumption_authorizations ("PlanReservationId", "ClaimId", "DeploymentId", "ContextTokens", "AuthorizedAt")
                            VALUES ({reservationId}, {claimId}, {deploymentId}, {100}, {now})
                            """);
                    db.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = reservationId, ClaimId = claimId,
                        DeploymentId = deploymentId, ConsumedAt = now });
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
            }
            return new(factory, reservationId, claimId, deploymentId, planId, tokenId);
        }

        private static CloudQuotaBucket Bucket(Guid id, Guid tenant, Guid app, string unit, DateTimeOffset now) =>
            new() { Id = id, TenantId = tenant, ApplicationId = app, CapabilityId = "inference.liveui", Unit = unit,
                PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = 1000 };
    }

    private sealed class DbFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
