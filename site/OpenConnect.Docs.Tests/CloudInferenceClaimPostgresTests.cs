using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
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
            var privacyEvidence = new EfCloudInferencePrivacyEvidenceStore(factory, TimeProvider.System);
            var machine = new CloudCredentialIdentity(credential, tenant.Id, app.Id, "Development", new HashSet<string> { "inference.chat" });
            async Task<Guid> Reserve(bool inspected = true)
            {
                var requestId = Guid.NewGuid();
                var reservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "inference.chat", "token", requestId, "key", new byte[32], 5, now.AddMinutes(5)), default);
                Assert.Equal("reserved", reservation.Status);
                if (inspected) await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "allowed", new string('a', 32), default);
                return reservation.ReservationId!.Value;
            }
            Task<CloudExecutionClaimResult> Claim(Guid reservation, string? model) => claims.ClaimAsync(credential, tenant.Id, app.Id, reservation, "inference.chat", "key", new byte[32], null, default, workerPayloadFingerprint: new byte[32], inferenceModelId: model, inferenceDeployment: match);
            var id = await Reserve(inspected: false);
            Assert.Equal("invalid_request", (await claims.ClaimAsync(credential, tenant.Id, app.Id, id, "inference.chat", "key", new byte[32], null, default, inferenceModelId: "registry/model-a", inferenceDeployment: match)).Status);
            Assert.Equal("invalid_request", (await Claim(id, null)).Status);
            Assert.Equal("invalid_request", (await Claim(id, "bad model")).Status);
            Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 0, default)).Status);
            Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var requestId = (await db.QuotaReservations.SingleAsync(x => x.Id == id)).RequestId;
                await privacyEvidence.AppendAsync(machine, requestId, "other", new byte[32], "allowed", new string('a', 32), default);
                Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
                await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "allowed", new string('a', 32), default);
                await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "privacy_blocked", new string('a', 32), default);
                Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
                await privacyEvidence.AppendAsync(machine, requestId, "key", Enumerable.Repeat((byte)1, 32).ToArray(), "allowed", new string('a', 32), default);
                Assert.Equal("unavailable", (await Claim(id, "registry/model-a")).Status);
                await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "allowed", new string('a', 32), default);
            }
            await using (var evidenceWriter = await factory.CreateDbContextAsync())
            {
                await using var transaction = await evidenceWriter.Database.BeginTransactionAsync();
                await evidenceWriter.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {app.Id} AND \"TenantId\" = {tenant.Id} FOR UPDATE").AsNoTracking().SingleAsync();
                var requestId = (await evidenceWriter.QuotaReservations.SingleAsync(x => x.Id == id)).RequestId;
                var evidenceId = Guid.NewGuid(); var trace = new string('a', 32); var bytes = new byte[32];
                await evidenceWriter.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO cloud.inference_privacy_evidence ("Id", "TenantId", "ApplicationId", "RequestId", "Outcome", "TraceId", "OccurredAt", "FingerprintKeyId", "RequestFingerprint")
                    VALUES ({evidenceId}, {tenant.Id}, {app.Id}, {requestId}, {"privacy_blocked"}, {trace}, {DateTimeOffset.UtcNow}, {"key"}, {bytes})
                    """);
                var pending = Claim(id, "registry/model-a");
                await Task.Delay(100);
                Assert.False(pending.IsCompleted);
                await transaction.CommitAsync();
                Assert.Equal("unavailable", (await pending).Status);
                await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "allowed", new string('a', 32), default);
            }
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
            var workerReadiness = new EfCloudWorkerReadinessStore(factory);
            Assert.True(await workerReadiness.IsReadyAsync(deploymentId, default));
            var access = new EfCloudInferenceAccessStore(factory, TimeProvider.System);
            Assert.True(await access.IsClaimAllowedAsync(machine, id, firstClaim.ClaimId!.Value, default));
            Assert.False(await access.IsClaimAllowedAsync(machine with { CredentialId = Guid.NewGuid() }, id, firstClaim.ClaimId.Value, default));
            Assert.False(await access.IsClaimAllowedAsync(machine with { ApplicationId = Guid.NewGuid() }, id, firstClaim.ClaimId.Value, default));
            await using (var db = await factory.CreateDbContextAsync())
                await db.WorkerDeployments.Where(x => x.Id == deploymentId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            Assert.False(await workerReadiness.IsReadyAsync(deploymentId, default));
            Assert.False(await access.IsClaimAllowedAsync(machine, id, firstClaim.ClaimId.Value, default));
            await using (var db = await factory.CreateDbContextAsync())
                await db.WorkerDeployments.Where(x => x.Id == deploymentId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[]"));
            Assert.False(await access.IsClaimAllowedAsync(machine, id, firstClaim.ClaimId.Value, default));
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-a\"]"));
            var consumption = new CloudInferenceConsumptionService(new EfCloudInferenceConsumptionStore(factory, TimeProvider.System));
            Assert.Equal("unavailable", (await consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, Guid.NewGuid(), new byte[32], default)).Status);
            Assert.Equal("unavailable", (await consumption.ConsumeAsync(id, firstClaim.ClaimId.Value, deploymentId, Enumerable.Repeat((byte)1, 32).ToArray(), default)).Status);
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
            Assert.Equal("unavailable", (await consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, deploymentId, new byte[32], default)).Status);
            Assert.False(await access.IsClaimAllowedAsync(machine, id, firstClaim.ClaimId.Value, default));
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 2, default)).Status);
            Assert.True(await access.IsClaimAllowedAsync(machine, id, firstClaim.ClaimId.Value, default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var requestId = (await db.QuotaReservations.SingleAsync(x => x.Id == id)).RequestId;
                await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "privacy_blocked", new string('a', 32), default);
                Assert.Equal("unavailable", (await consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, deploymentId, new byte[32], default)).Status);
                await privacyEvidence.AppendAsync(machine, requestId, "key", new byte[32], "allowed", new string('a', 32), default);
            }
            var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => consumption.ConsumeAsync(id, firstClaim.ClaimId!.Value, deploymentId, new byte[32], default)));
            Assert.Single(attempts, x => x.Status == "consumed" && x.ReservedTokens == 5 && x.ModelId == "registry/model-a" && x.ModelRevision == "revision-1" && x.ModelDigest == "sha256:" + new string('a', 64));
            Assert.Equal(9, attempts.Count(x => x.Status == "already_consumed" && x.ReservedTokens is null && x.ModelId is null && x.ModelRevision is null && x.ModelDigest is null));
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
            var outputEvidence = new EfCloudInferenceOutputEvidenceStore(factory, TimeProvider.System);
            Guid outputRequestId;
            await using (var db = await factory.CreateDbContextAsync()) outputRequestId = (await db.QuotaReservations.SingleAsync(x => x.Id == id)).RequestId;
            var outputTrace = new string('a', 32);
            Assert.False(await outputEvidence.AppendForCompletedAsync(machine, outputRequestId, id, firstClaim.ClaimId!.Value, "allowed", outputTrace, default));
            Assert.Equal("invalid_request", await settlement.SettleAsync(id, firstClaim.ClaimId!.Value, deploymentId, new(-1, 0), default));
            Assert.Equal("invalid_request", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(long.MaxValue, 1), default));
            Assert.Equal("unavailable", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, Guid.NewGuid(), new(2, 1), default));
            Assert.Equal("overage_requires_reconciliation", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(3, 3), default));
            var completions = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(2, 1), default)));
            Assert.Single(completions, x => x == "completed");
            Assert.Equal(9, completions.Count(x => x == "already_completed"));
            Assert.Equal("conflict", await settlement.SettleAsync(id, firstClaim.ClaimId.Value, deploymentId, new(1, 2), default));
            Assert.True(await access.IsClaimAllowedAsync(machine, id, firstClaim.ClaimId.Value, default));
            Assert.False(await outputEvidence.AppendForCompletedAsync(machine with { CredentialId = Guid.NewGuid() }, outputRequestId, id, firstClaim.ClaimId.Value, "allowed", outputTrace, default));
            Assert.False(await outputEvidence.AppendForCompletedAsync(machine with { TenantId = Guid.NewGuid() }, outputRequestId, id, firstClaim.ClaimId.Value, "allowed", outputTrace, default));
            Assert.False(await outputEvidence.AppendForCompletedAsync(machine with { ApplicationId = Guid.NewGuid() }, outputRequestId, id, firstClaim.ClaimId.Value, "allowed", outputTrace, default));
            Assert.False(await outputEvidence.AppendForCompletedAsync(machine, Guid.NewGuid(), id, firstClaim.ClaimId.Value, "allowed", outputTrace, default));
            Assert.True(await outputEvidence.AppendForCompletedAsync(machine, outputRequestId, id, firstClaim.ClaimId.Value, "allowed", outputTrace, default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.inference_output_privacy_evidence WHERE \"ReservationId\" = {id}").SingleAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.inference_output_privacy_evidence WHERE \"ReservationId\" = {id}"));
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                var saved = await db.QuotaReservations.SingleAsync(x => x.Id == id);
                Assert.Equal(3, saved.Actual);
                Assert.Equal(3, (await db.QuotaBuckets.SingleAsync(x => x.Id == saved.BucketId)).Consumed);
                Assert.Equal(3, (await db.UsageRecords.SingleAsync(x => x.ReservationId == id)).Quantity);
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.inference_token_observations SET \"InputTokens\" = 0 WHERE \"ReservationId\" = {id}"));
            }
            await VerifyGatewayApiAsync(factory, owner, tenant.Id, app.Id, now);
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
    private static async Task VerifyGatewayApiAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, CloudSubject owner, Guid tenantId, Guid appId, DateTimeOffset now)
    {
        var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
        var issued = await credentials.CreateAsync(owner, tenantId, appId, ["inference.chat"], now.AddHours(1), default);
        Assert.Equal("succeeded", issued.Status);
        var deploymentId = Guid.NewGuid(); var reference = "http-" + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "inference.chat", EndpointReference = reference, Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = "registry/model-a", ModelRevision = "revision-1", ModelDigest = "sha256:" + new string('a', 64), ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = Guid.NewGuid(), CapabilityId = "inference.chat", EndpointReference = reference + "-admission", Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = "registry/model-a", ModelRevision = "revision-1", ModelDigest = "sha256:" + new string('a', 64), ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            await db.SaveChangesAsync();
        }
        using var server = Certificate(false); using var gateway = Certificate(true); using var other = Certificate(true);
        var trust = new CloudGatewayClientTrust([Convert.ToHexString(gateway.GetCertHash(HashAlgorithmName.SHA256))]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(server, https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (_, _, _) => true;
        })));
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>(); builder.Services.AddScoped<CloudCredentialService>();
        builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>(); builder.Services.AddScoped<CloudMachineAuthorizationService>();
        builder.Services.AddScoped<ICloudQuotaStore, EfCloudQuotaStore>(); builder.Services.AddScoped<CloudQuotaService>();
        builder.Services.AddScoped<ICloudExecutionClaimStore, EfCloudExecutionClaimStore>(); builder.Services.AddScoped<CloudExecutionClaimService>();
        builder.Services.AddSingleton(new CloudInferencePrivacyService(new RulesSecurityDetector()));
        builder.Services.AddSingleton(new CloudInferenceOutputPrivacyService(new RulesSecurityDetector()));
        builder.Services.AddScoped<ICloudInferenceOutputEvidenceStore, EfCloudInferenceOutputEvidenceStore>();
        builder.Services.AddScoped<ICloudInferenceAccessStore, EfCloudInferenceAccessStore>();
        var evidence = new SwitchableInferenceEvidence(new EfCloudInferencePrivacyEvidenceStore(factory, TimeProvider.System));
        builder.Services.AddSingleton<ICloudInferencePrivacyEvidenceStore>(evidence);
        await using var host = builder.Build();
        host.MapCloudGatewayAuthorization(trust, true);
        host.MapCloudInferencePrivacy(trust, true);
        host.MapCloudInferenceOutputPrivacy(trust, true);
        host.MapCloudInferenceAccess(trust, true);
        host.MapCloudInferenceFingerprintKeyLookup(trust, true); host.MapCloudInferenceQuota(trust, true); host.MapCloudInferenceClaim(trust, true);
        await host.StartAsync();
        var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var client = Client(address, server, gateway); using var untrusted = Client(address, server, other);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
        untrusted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
        var privacyId = Guid.NewGuid();
        async Task<HttpResponseMessage> Inspect(HttpClient sender, string body, Guid? targetId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/inference/privacy") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Cloud-Request-Id", (targetId ?? privacyId).ToString("D"));
            request.Headers.Add("X-Cloud-Fingerprint-Key-Id", "key");
            request.Headers.Add("X-Cloud-Request-Fingerprint", Convert.ToBase64String(new byte[32]));
            return await sender.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Inspect(untrusted, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/cloud/internal/v1/inference/privacy", new { messages = new[] { new { role = "user", content = "hello" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Inspect(client, "{\"messages\":[{\"role\":\"user\",\"content\":\"x\",\"Content\":\"y\"}]}" )).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"messages\":[{\"role\":\"user\",\"content\":\"hello world\"}]}" )).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Inspect(client, "{\"messages\":[{\"role\":\"user\",\"content\":\"person@\"},{\"role\":\"assistant\",\"content\":\"example.test\"}]}" )).StatusCode);
        evidence.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Inspect(client, "{\"messages\":[{\"role\":\"user\",\"content\":\"hello world\"}]}" )).StatusCode);
        evidence.Fail = false;
        var unavailable = new CloudInferencePrivacyService(null);
        var identity = new CloudCredentialIdentity(issued.Credential!.Id, tenantId, appId, "Development", new HashSet<string> { "inference.chat" });
        Assert.Equal("detector_unavailable", (await unavailable.EvaluateAsync(identity, privacyId, [new("user", "hello")], default)).Code);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.inference_privacy_evidence WHERE \"RequestId\" = {privacyId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {appId}").SingleAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.inference_privacy_evidence WHERE \"RequestId\" = {privacyId}"));
        }
        var requestId = Guid.NewGuid(); var fingerprint = Convert.ToBase64String(new byte[32]);
        var path = "/cloud/internal/v1/inference/quota/reservations";
        var payload = new { requestId, fingerprintKeyId = "key", fingerprint, expiresAt = now.AddMinutes(5), reservedTokens = 5 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.PostAsJsonAsync(path, payload)).StatusCode);
        using (var duplicate = new StringContent("{\"reservedTokens\":1,\"ReservedTokens\":2}", System.Text.Encoding.UTF8, "application/json"))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, duplicate)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { requestId, fingerprintKeyId = "key", fingerprint, expiresAt = now.AddMinutes(5), reservedTokens = 0 })).StatusCode);
        var response = await client.PostAsJsonAsync(path, payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var reservation = await response.Content.ReadFromJsonAsync<CloudQuotaResult>();
        Assert.Equal("reserved", reservation!.Status);
        Assert.Equal("duplicate", (await (await client.PostAsJsonAsync(path, payload)).Content.ReadFromJsonAsync<CloudQuotaResult>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { requestId, fingerprintKeyId = "key", fingerprint, expiresAt = now.AddMinutes(5), reservedTokens = 6 })).StatusCode);
        var lookup = await client.GetFromJsonAsync<CloudFingerprintKeyLookup>($"/cloud/internal/v1/inference/quota/fingerprint-key/{requestId:D}");
        Assert.Equal("found", lookup!.Status); Assert.Equal("key", lookup.KeyId);
        var claimPath = $"{path}/{reservation.ReservationId:D}/claim";
        var proof = new { fingerprintKeyId = "key", fingerprint, endpointReference = reference, region = "region-a", contractVersion = "openconnect.v1", modelRevision = "revision-1", modelDigest = "sha256:" + new string('a', 64), modelId = "registry/model-a", workerPayloadFingerprint = fingerprint };
        Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.PostAsJsonAsync(claimPath, proof)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimPath, new { fingerprintKeyId = "key", fingerprint, endpointReference = reference, region = "region-a", contractVersion = "openconnect.v1", modelRevision = "revision-1", modelDigest = "sha256:" + new string('a', 64), modelId = "registry/model-a" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(claimPath, proof)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"messages\":[{\"role\":\"user\",\"content\":\"hello world\"}]}", requestId)).StatusCode);
        var claim = await client.PostAsJsonAsync(claimPath, proof);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var claimResult = await claim.Content.ReadFromJsonAsync<CloudExecutionClaimResult>();
        Assert.Equal("claimed", claimResult!.Status);
        var claimId = claimResult.ClaimId!.Value;
        var accessPath = $"/cloud/internal/v1/inference/claims/{reservation.ReservationId!.Value:D}/{claimId:D}/access";
        Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.GetAsync(accessPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(accessPath)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/cloud/internal/v1/inference/claims/{reservation.ReservationId.Value:D}/{Guid.NewGuid():D}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(claimPath, proof)).StatusCode);
        await VerifyNodeClientAsync(address, server, gateway, issued.Secret!, requestId, reservation.ReservationId!.Value, now.AddMinutes(5), reference);
        await VerifyNodeAdmissionAsync(address, server, gateway, issued.Secret!, reference + "-admission");
        async Task<HttpResponseMessage> InspectOutput(HttpClient sender, string body, Guid targetClaim)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/inference/output-privacy")
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Cloud-Request-Id", requestId.ToString("D"));
            message.Headers.Add("X-Cloud-Reservation-Id", reservation.ReservationId!.Value.ToString("D"));
            message.Headers.Add("X-Cloud-Claim-Id", targetClaim.ToString("D"));
            return await sender.SendAsync(message);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await InspectOutput(untrusted, "{\"output\":\"safe\"}", claimId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await InspectOutput(client, "{\"output\":\"safe\",\"output\":\"other\"}", claimId)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await InspectOutput(client, "{\"output\":\"safe\"}", claimId)).StatusCode);
        var consumption = new CloudInferenceConsumptionService(new EfCloudInferenceConsumptionStore(factory, TimeProvider.System));
        Assert.Equal("consumed", (await consumption.ConsumeAsync(reservation.ReservationId!.Value, claimId, deploymentId, new byte[32], default)).Status);
        var settlement = new CloudInferenceTokenSettlement(new EfCloudInferenceTokenSettlementStore(factory, TimeProvider.System));
        Assert.Equal("completed", await settlement.SettleAsync(reservation.ReservationId.Value, claimId, deploymentId, new(2, 1), default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await InspectOutput(client, "{\"output\":\"safe\"}", Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await InspectOutput(client, "{\"output\":\"safe\"}", claimId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await InspectOutput(client, "{\"output\":\"person@example.test\"}", claimId)).StatusCode);
        await VerifyNodeOutputPrivacyAsync(address, server, gateway, issued.Secret!, requestId, reservation.ReservationId.Value, claimId);
        Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenantId, appId, issued.Credential!.Id, default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(accessPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/cloud/internal/v1/inference/quota/fingerprint-key/{requestId:D}")).StatusCode);
    }
    private static async Task VerifyNodeOutputPrivacyAsync(Uri address, X509Certificate2 server, X509Certificate2 gateway, string credential, Guid requestId, Guid reservationId, Guid claimId)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE);const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY});const x=JSON.parse(process.env.OC_TEST_DATA);const a=await c.inspectInferenceOutput(process.env.OC_TEST_CREDENTIAL,x.requestId,x.reservationId,x.claimId,'safe');const b=await c.inspectInferenceOutput(process.env.OC_TEST_CREDENTIAL,x.requestId,x.reservationId,x.claimId,'person@example.test');const access=await c.verifyInferenceClaim(process.env.OC_TEST_CREDENTIAL,x.reservationId,x.claimId);process.stdout.write(JSON.stringify([a.code,b.code,access]));";
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString(); start.Environment["OC_TEST_CA"] = server.ExportCertificatePem(); start.Environment["OC_TEST_CERT"] = gateway.ExportCertificatePem();
        using var privateKey = gateway.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem(); start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_DATA"] = System.Text.Json.JsonSerializer.Serialize(new { requestId, reservationId, claimId });
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, errors); Assert.Equal("[\"allowed\",\"privacy_blocked\",true]", output);
    }
    private static async Task VerifyNodeAdmissionAsync(Uri address, X509Certificate2 server, X509Certificate2 gateway, string credential, string reference)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE);const a=await import(new URL('./inference-admission.mjs',process.env.OC_TEST_MODULE));const f=await import(new URL('./request-fingerprint.mjs',process.env.OC_TEST_MODULE));const crypto=await import('node:crypto');const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY});const auth=await c.authorize(process.env.OC_TEST_CREDENTIAL,'inference.chat');const id=crypto.randomUUID();const input={schemaVersion:'openconnect.cloud.inference/v1',requestId:id,capability:'inference.chat',expiresAt:new Date(Date.now()+60000).toISOString(),modelId:'registry/model-a',input:{messages:[{role:'user',content:'safe fixture'}],maxTokens:2,stream:false}};const ring=f.createFingerprintKeyring({activeKeyId:'key',keys:[{id:'key',key:Buffer.alloc(32,7)}]});const quote=async({payload,key})=>({requestId:id,modelId:input.modelId,maxOutputTokens:2,inputTokenUpperBound:1,workerPayloadFingerprint:crypto.createHmac('sha256',key).update(payload).digest('base64')});try{const r=await a.withInferenceAdmission({credential:process.env.OC_TEST_CREDENTIAL,identity:auth.identity,body:Buffer.from(JSON.stringify(input)),deployment:{endpointReference:process.env.OC_TEST_REFERENCE,region:'region-a',contractVersion:'openconnect.v1',modelRevision:'revision-1',modelDigest:'sha256:'+'a'.repeat(64)}},{controlPlane:c,keyring:ring,quote},async()=>({status:'test_claimed'}));process.stdout.write(r.status);}finally{ring.dispose();}";
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString(); start.Environment["OC_TEST_CA"] = server.ExportCertificatePem(); start.Environment["OC_TEST_CERT"] = gateway.ExportCertificatePem();
        using var privateKey = gateway.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem(); start.Environment["OC_TEST_CREDENTIAL"] = credential; start.Environment["OC_TEST_REFERENCE"] = reference;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, errors); Assert.Equal("test_claimed", output);
    }
    private static async Task VerifyNodeClientAsync(Uri address, X509Certificate2 server, X509Certificate2 gateway, string credential, Guid requestId, Guid reservationId, DateTimeOffset expiresAt, string reference)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE);const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY});const x=JSON.parse(process.env.OC_TEST_DATA);const a=await c.findInferenceFingerprintKey(process.env.OC_TEST_CREDENTIAL,x.requestId);const b=await c.reserveInference(process.env.OC_TEST_CREDENTIAL,x.reservation);const d=await c.claimInference(process.env.OC_TEST_CREDENTIAL,x.reservationId,x.proof);const p=await c.inspectInference(process.env.OC_TEST_CREDENTIAL,x.requestId,[{role:'user',content:'hello world'}], 'key',x.reservation.fingerprint);process.stdout.write(JSON.stringify([a.status,b.status,d.status,p.code]));";
        var fingerprint = Convert.ToBase64String(new byte[32]);
        var data = new { requestId, reservationId, reservation = new { requestId, fingerprintKeyId = "key", fingerprint, expiresAt, reservedTokens = 5 },
            proof = new { fingerprintKeyId = "key", fingerprint, workerPayloadFingerprint = fingerprint, modelId = "registry/model-a", deployment = new { endpointReference = reference, region = "region-a", contractVersion = "openconnect.v1", modelRevision = "revision-1", modelDigest = "sha256:" + new string('a', 64) } } };
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString(); start.Environment["OC_TEST_CA"] = server.ExportCertificatePem(); start.Environment["OC_TEST_CERT"] = gateway.ExportCertificatePem();
        using var privateKey = gateway.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem(); start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_DATA"] = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, errors);
        Assert.Equal(new[] { "found", "duplicate", "already_claimed", "allowed" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(output));
    }
    private sealed class SwitchableInferenceEvidence(ICloudInferencePrivacyEvidenceStore inner) : ICloudInferencePrivacyEvidenceStore
    {
        public bool Fail { get; set; }
        public Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint, string outcome, string traceId, CancellationToken cancellationToken) =>
            Fail ? Task.FromException(new InvalidOperationException("unavailable")) : inner.AppendAsync(identity, requestId, fingerprintKeyId, fingerprint, outcome, traceId, cancellationToken);
    }
    private static X509Certificate2 Certificate(bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client) { var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build()); }
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var data = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(data, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    private static HttpClient Client(Uri address, X509Certificate2 server, X509Certificate2 client)
    {
        var expected = server.GetCertHash(HashAlgorithmName.SHA256);
        var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256)) };
        handler.ClientCertificates.Add(client);
        return new HttpClient(handler) { BaseAddress = address };
    }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
