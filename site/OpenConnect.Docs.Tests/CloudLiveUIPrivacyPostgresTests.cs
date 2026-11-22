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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudLiveUIPrivacyPostgresTests
{
    [CloudPostgresFact]
    public async Task GatewayIdentityAndCredentialAreRequiredAndEvidenceContainsNoRawFields()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "LiveUI privacy", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Fixture", "Development", default)).Application!;
        string previousAvailability, previousRegions;
        bool previousRegionsVerified;
        var deploymentId = Guid.NewGuid();
        var deploymentRef = "liveui-" + Guid.NewGuid().ToString("N");
        var modelDigest = "sha256:" + new string('a', 64);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.liveui");
            previousAvailability = capability.PlatformAvailability;
            previousRegions = capability.AllowedRegions;
            previousRegionsVerified = capability.AllowedRegionsVerified;
            capability.PlatformAvailability = "available";
            capability.AllowedRegions = "[\"region-a\"]";
            capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deploymentId,
                CapabilityId = "inference.liveui",
                EndpointReference = deploymentRef,
                Transport = "https-mtls",
                Region = "region-a",
                ContractVersion = "v1",
                ModelId = "registry/model-a",
                ModelRevision = "revision-1",
                ModelDigest = modelDigest,
                ImageDigest = "sha256:" + new string('b', 64),
                Enabled = true,
                ConcurrencyLimit = 1
            });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "inference.liveui", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "inference.liveui", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.liveui", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                CapabilityId = "inference.liveui",
                Unit = "plan",
                PeriodStart = DateTimeOffset.UtcNow.AddMinutes(-1),
                PeriodEnd = DateTimeOffset.UtcNow.AddHours(1),
                Limit = 2
            });
            await db.SaveChangesAsync();
        }
        try
        {
            var policies = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 0, default)).Status);
            var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
            var issued = await credentials.CreateAsync(owner, tenant.Id, app.Id, ["inference.liveui"], DateTimeOffset.UtcNow.AddHours(1), default);
            Assert.Equal("succeeded", issued.Status);
            using var server = Certificate(false);
            using var gateway = Certificate(true);
            using var other = Certificate(true);
            using var worker = Certificate(true);
            var trust = new CloudGatewayClientTrust([Convert.ToHexString(gateway.GetCertHash(HashAlgorithmName.SHA256))]);
            var workerTrust = new CloudWorkerClientTrust([new(deploymentId, Convert.ToHexString(worker.GetCertHash(HashAlgorithmName.SHA256)))], trust);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(server, https =>
            {
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            })));
            builder.Services.AddSingleton(factory);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>();
            builder.Services.AddScoped<CloudCredentialService>();
            builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>();
            builder.Services.AddScoped<CloudMachineAuthorizationService>();
            builder.Services.AddSingleton(new CloudLiveUIPrivacyService(new RulesSecurityDetector()));
            builder.Services.AddScoped<ICloudLiveUIPrivacyEvidenceStore, EfCloudLiveUIPrivacyEvidenceStore>();
            builder.Services.AddScoped<ICloudQuotaStore, EfCloudQuotaStore>();
            builder.Services.AddScoped<CloudQuotaService>();
            builder.Services.AddScoped<ICloudLiveUIDualQuotaStore, EfCloudLiveUIDualQuotaStore>();
            builder.Services.AddScoped<CloudLiveUIDualQuotaService>();
            builder.Services.AddScoped<ICloudLiveUIClaimStore, EfCloudLiveUIClaimStore>();
            builder.Services.AddScoped<CloudLiveUIClaimService>();
            builder.Services.AddScoped<ICloudLiveUIConsumptionStore, EfCloudLiveUIConsumptionStore>();
            builder.Services.AddScoped<CloudLiveUIConsumptionService>();
            builder.Services.AddScoped<ICloudLiveUIMeteredConsumptionStore, EfCloudLiveUIConsumptionStore>();
            builder.Services.AddScoped<CloudLiveUIMeteredConsumption>();
            builder.Services.AddScoped<ICloudLiveUISettlementStore, EfCloudLiveUISettlementStore>();
            builder.Services.AddScoped<CloudLiveUISettlement>();
            builder.Services.AddScoped<ICloudLiveUIDualSettlementStore, EfCloudLiveUIDualSettlementStore>();
            builder.Services.AddScoped<CloudLiveUIDualSettlement>();
            builder.Services.AddSingleton(new CloudLiveUIOutputPrivacyService(new RulesSecurityDetector()));
            builder.Services.AddScoped<ICloudLiveUIOutputEvidenceStore, EfCloudLiveUIOutputEvidenceStore>();
            builder.Services.AddScoped<ICloudLiveUIAccessStore, EfCloudLiveUIAccessStore>();
            builder.Services.AddScoped<ICloudWorkerReadinessStore, EfCloudWorkerReadinessStore>();
            builder.Services.AddScoped<CloudWorkerReadiness>();
            await using var host = builder.Build();
            host.MapCloudLiveUIPrivacy(trust, true);
            host.MapCloudLiveUIQuota(trust, true);
            host.MapCloudLiveUIClaim(trust, true);
            host.MapCloudLiveUIConsumption(workerTrust, true);
            host.MapCloudLiveUISettlement(workerTrust, true);
            host.MapCloudWorkerReadiness(workerTrust, true);
            host.MapCloudLiveUIMeteredConsumption(workerTrust, true);
            host.MapCloudLiveUIDualSettlement(workerTrust, true);
            host.MapCloudLiveUIDualQuota(trust, true);
            host.MapCloudLiveUIOutputPrivacy(trust, true);
            host.MapCloudLiveUIAccess(trust, true);
            await host.StartAsync();
            var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = Client(address, server, gateway);
            using var untrusted = Client(address, server, other);
            using var workerClient = Client(address, server, worker);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
            untrusted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
            var requestId = Guid.NewGuid();
            async Task<HttpResponseMessage> Inspect(HttpClient sender, string body, Guid? target = null)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/liveui/privacy")
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                message.Headers.Add("X-Cloud-Request-Id", (target ?? requestId).ToString("D"));
                message.Headers.Add("X-Cloud-Fingerprint-Key-Id", "key");
                message.Headers.Add("X-Cloud-Request-Fingerprint", Convert.ToBase64String(new byte[32]));
                return await sender.SendAsync(message);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Inspect(untrusted, "{\"fields\":[\"v1\",\"safe\",\"route\"]}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Inspect(client, "{\"fields\":[],\"Fields\":[]}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"fields\":[\"v1\",\"show tasks\",\"route.root\"]}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Inspect(client, "{\"fields\":[\"v1\",\"person@\",\"example.test\"]}")).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_privacy_evidence WHERE \"RequestId\" = {requestId} AND \"TenantId\" = {tenant.Id} AND \"ApplicationId\" = {app.Id}").SingleAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.liveui_privacy_evidence WHERE \"RequestId\" = {requestId}"));
            }
            await VerifyNodePrivacyAsync(address, server, gateway, issued.Secret!, requestId);
            var quotaPath = "/cloud/internal/v1/liveui/quota/reservations";
            var expiry = DateTimeOffset.UtcNow.AddMinutes(3);
            var first = new { requestId = Guid.NewGuid(), fingerprintKeyId = "key", fingerprint = Convert.ToBase64String(new byte[32]), expiresAt = expiry };
            Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.PostAsJsonAsync(quotaPath, first)).StatusCode);
            Assert.Equal("not_found", (await client.GetFromJsonAsync<CloudFingerprintKeyLookup>($"/cloud/internal/v1/liveui/quota/fingerprint-key/{first.requestId:D}"))!.Status);
            var reserved = await (await client.PostAsJsonAsync(quotaPath, first)).Content.ReadFromJsonAsync<CloudQuotaResult>();
            Assert.Equal("reserved", reserved!.Status);
            Assert.Equal("duplicate", (await (await client.PostAsJsonAsync(quotaPath, first)).Content.ReadFromJsonAsync<CloudQuotaResult>())!.Status);
            Assert.Equal("found", (await client.GetFromJsonAsync<CloudFingerprintKeyLookup>($"/cloud/internal/v1/liveui/quota/fingerprint-key/{first.requestId:D}"))!.Status);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(quotaPath, first with { fingerprint = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) })).StatusCode);
            var second = first with { requestId = Guid.NewGuid() };
            var secondReserved = await (await client.PostAsJsonAsync(quotaPath, second)).Content.ReadFromJsonAsync<CloudQuotaResult>();
            Assert.Equal("reserved", secondReserved!.Status);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(quotaPath, first with { requestId = Guid.NewGuid() })).StatusCode);
            Assert.True(CloudQuotaUnits.Supports("inference.liveui", "plans and tokens", "plan"));
            var proof = new
            {
                fingerprintKeyId = "key",
                fingerprint = first.fingerprint,
                endpointReference = deploymentRef,
                region = "region-a",
                contractVersion = "v1",
                modelRevision = "revision-1",
                modelDigest,
                modelId = "registry/model-a",
                workerPayloadFingerprint = first.fingerprint
            };
            var claimPath = $"{quotaPath}/{reserved.ReservationId!.Value:D}/claim";
            Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.PostAsJsonAsync(claimPath, proof)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(claimPath, proof)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"fields\":[\"v1\",\"safe\",\"route\"]}", first.requestId)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(claimPath, proof with { fingerprint = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) })).StatusCode);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync(claimPath, proof)));
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, attempts.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            var claim = await attempts.Single(x => x.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<CloudExecutionClaimResult>();
            var accessPath = $"/cloud/internal/v1/liveui/claims/{reserved.ReservationId.Value:D}/{claim!.ClaimId!.Value:D}/access";
            Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.GetAsync(accessPath)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(accessPath)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"fields\":[\"v1\",\"safe\",\"route\"]}", second.requestId)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"{quotaPath}/{secondReserved.ReservationId!.Value:D}/claim", proof)).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ExecutionClaims.Add(new CloudExecutionClaim
                {
                    ReservationId = secondReserved.ReservationId.Value,
                    ClaimId = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    ApplicationId = app.Id,
                    DeploymentId = deploymentId,
                    Region = "region-a",
                    ClaimedAt = DateTimeOffset.UtcNow
                });
                await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            }
            var consumptionPath = $"/cloud/internal/v1/liveui/quota/reservations/{reserved.ReservationId.Value:D}/claims/{claim!.ClaimId!.Value:D}/consume";
            var completionPath = $"/cloud/internal/v1/liveui/quota/reservations/{reserved.ReservationId.Value:D}/claims/{claim.ClaimId.Value:D}/complete";
            async Task<HttpResponseMessage> InspectOutput(HttpClient sender, string body, Guid targetClaim)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/liveui/output-privacy")
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                message.Headers.Add("X-Cloud-Request-Id", first.requestId.ToString("D"));
                message.Headers.Add("X-Cloud-Reservation-Id", reserved.ReservationId.Value.ToString("D"));
                message.Headers.Add("X-Cloud-Claim-Id", targetClaim.ToString("D"));
                return await sender.SendAsync(message);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await InspectOutput(untrusted, "{\"fields\":[\"v1\",\"activate\",\"safe\"]}", claim.ClaimId.Value)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await InspectOutput(client, "{\"fields\":[\"v1\",\"activate\",\"safe\"]}", claim.ClaimId.Value)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(completionPath, null)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await workerClient.PostAsync(completionPath, null)).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.ExecutionCompletions.Add(new CloudExecutionCompletion
                {
                    ReservationId = reserved.ReservationId.Value,
                    ClaimId = claim.ClaimId.Value,
                    Outcome = "completed",
                    CompletedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
                await Assert.ThrowsAnyAsync<Exception>(() => db.QuotaReservations.Where(x => x.Id == reserved.ReservationId.Value)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 1L).SetProperty(y => y.FinalizedAt, DateTimeOffset.UtcNow)));
                await transaction.RollbackAsync();
            }
            async Task<HttpResponseMessage> Consume(HttpClient sender, byte[] binding)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, consumptionPath);
                message.Headers.Add("X-OpenConnect-Worker-Payload-Fingerprint", Convert.ToBase64String(binding));
                return await sender.SendAsync(message);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Consume(client, new byte[32])).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await workerClient.GetAsync("/cloud/internal/v1/worker/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Consume(workerClient, Enumerable.Repeat((byte)1, 32).ToArray())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Inspect(client, "{\"fields\":[\"v1\",\"person@\",\"example.test\"]}", first.requestId)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(accessPath)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Consume(workerClient, new byte[32])).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"fields\":[\"v1\",\"safe\",\"route\"]}", first.requestId)).StatusCode);
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, [], 1, default)).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(accessPath)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Consume(workerClient, new byte[32])).StatusCode);
            Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["registry/model-a"], 2, default)).Status);
            var consumes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Consume(workerClient, new byte[32])));
            Assert.Single(consumes, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, consumes.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            var consumed = await consumes.Single(x => x.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<CloudLiveUIConsumptionResult>();
            Assert.Equal("registry/model-a", consumed!.ModelId);
            Assert.Equal("revision-1", consumed.ModelRevision);
            Assert.Equal(modelDigest, consumed.ModelDigest);
            var completions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => workerClient.PostAsync(completionPath, null)));
            var statuses = await Task.WhenAll(completions.Select(async response => (await response.Content.ReadFromJsonAsync<CloudExecutionCompletionResult>())!.Status));
            Assert.Single(statuses, x => x == "completed");
            Assert.Equal(7, statuses.Count(x => x == "already_completed"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(accessPath)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await InspectOutput(client, "{\"fields\":[\"v1\",\"activate\",\"safe\"]}", Guid.NewGuid())).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await InspectOutput(client, "{\"fields\":[\"v1\",\"activate\",\"safe\"]}", claim.ClaimId.Value)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await InspectOutput(client, "{\"fields\":[\"v1\",\"person@\",\"example.test\"]}", claim.ClaimId.Value)).StatusCode);
            var meteredApp = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Metered app", "Development", default)).Application!;
            var meteredNow = DateTimeOffset.UtcNow;
            var meteredTokenBucket = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection
                {
                    TenantId = tenant.Id,
                    ApplicationId = meteredApp.Id,
                    CapabilityId = "inference.liveui",
                    Enabled = true,
                    Revision = 1
                });
                db.QuotaBuckets.AddRange(
                    new CloudQuotaBucket
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenant.Id,
                        ApplicationId = meteredApp.Id,
                        CapabilityId = "inference.liveui",
                        Unit = "plan",
                        PeriodStart = meteredNow.AddMinutes(-1),
                        PeriodEnd = meteredNow.AddHours(1),
                        Limit = 2
                    },
                    new CloudQuotaBucket
                    {
                        Id = meteredTokenBucket,
                        TenantId = tenant.Id,
                        ApplicationId = meteredApp.Id,
                        CapabilityId = "inference.liveui",
                        Unit = "token",
                        PeriodStart = meteredNow.AddMinutes(-1),
                        PeriodEnd = meteredNow.AddHours(1),
                        Limit = 4096
                    });
                await db.SaveChangesAsync();
            }
            var meteredCredential = await credentials.CreateAsync(owner, tenant.Id, meteredApp.Id, ["inference.liveui"], meteredNow.AddHours(1), default);
            Assert.Equal("succeeded", meteredCredential.Status);
            using var meteredClient = Client(address, server, gateway);
            meteredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", meteredCredential.Secret);
            var meteredRequest = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, (await Inspect(meteredClient, "{\"fields\":[\"v1\",\"safe\",\"route\"]}", meteredRequest)).StatusCode);
            var dualPath = "/cloud/internal/v1/liveui/quota/dual-reservations";
            var dualRequest = new
            {
                requestId = meteredRequest,
                fingerprintKeyId = "key",
                fingerprint = Convert.ToBase64String(new byte[32]),
                tokenAmount = 2048,
                expiresAt = meteredNow.AddMinutes(3)
            };
            Assert.Equal(HttpStatusCode.Unauthorized, (await untrusted.PostAsJsonAsync(dualPath, dualRequest)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await meteredClient.PostAsJsonAsync(dualPath, dualRequest with { tokenAmount = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await meteredClient.PostAsync(dualPath,
                new StringContent("{\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"RequestId\":\"00000000-0000-0000-0000-000000000000\"}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            var dualReserved = await (await meteredClient.PostAsJsonAsync(dualPath, dualRequest)).Content.ReadFromJsonAsync<CloudQuotaResult>();
            Assert.Equal("reserved", dualReserved!.Status);
            Assert.Equal("duplicate", (await (await meteredClient.PostAsJsonAsync(dualPath, dualRequest)).Content.ReadFromJsonAsync<CloudQuotaResult>())!.Status);
            Assert.Equal(HttpStatusCode.Conflict, (await meteredClient.PostAsJsonAsync(dualPath, dualRequest with { tokenAmount = 2049 })).StatusCode);
            await VerifyNodeDualQuotaAsync(address, server, gateway, meteredCredential.Secret!, meteredRequest, dualRequest.expiresAt);
            var meteredClaim = await (await meteredClient.PostAsJsonAsync($"{quotaPath}/{dualReserved.ReservationId!.Value:D}/claim", proof)).Content.ReadFromJsonAsync<CloudExecutionClaimResult>();
            Assert.Equal("claimed", meteredClaim!.Status);
            var meteredPath = $"{quotaPath}/{dualReserved.ReservationId.Value:D}/claims/{meteredClaim.ClaimId!.Value:D}/consume-metered";
            using (var legacyMessage = new HttpRequestMessage(HttpMethod.Post,
                $"{quotaPath}/{dualReserved.ReservationId.Value:D}/claims/{meteredClaim.ClaimId.Value:D}/consume"))
            {
                legacyMessage.Headers.Add("X-OpenConnect-Worker-Payload-Fingerprint", Convert.ToBase64String(new byte[32]));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await workerClient.SendAsync(legacyMessage)).StatusCode);
            }
            async Task<HttpResponseMessage> ConsumeMetered(HttpClient sender, string budget, byte[] binding)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, meteredPath);
                message.Headers.Add("X-OpenConnect-Worker-Payload-Fingerprint", Convert.ToBase64String(binding));
                message.Headers.Add("X-OpenConnect-Worker-Context-Tokens", budget);
                return await sender.SendAsync(message);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await ConsumeMetered(meteredClient, "2048", new byte[32])).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await ConsumeMetered(workerClient, "02048", new byte[32])).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ConsumeMetered(workerClient, "4096", new byte[32])).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ConsumeMetered(workerClient, "2048", Enumerable.Repeat((byte)1, 32).ToArray())).StatusCode);
            var meteredAttempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ConsumeMetered(workerClient, "2048", new byte[32])));
            Assert.Single(meteredAttempts, item => item.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, meteredAttempts.Count(item => item.StatusCode == HttpStatusCode.Conflict));
            var meteredCompletionPath = $"{quotaPath}/{dualReserved.ReservationId.Value:D}/claims/{meteredClaim.ClaimId.Value:D}/complete-metered";
            var meteredObservationPath = $"{quotaPath}/{dualReserved.ReservationId.Value:D}/claims/{meteredClaim.ClaimId.Value:D}/observe-metered";
            Assert.Equal(HttpStatusCode.Unauthorized, (await meteredClient.PostAsJsonAsync(meteredObservationPath, new { inputTokens = 410, outputTokens = 31 })).StatusCode);
            var observed = await (await workerClient.PostAsJsonAsync(meteredObservationPath, new { inputTokens = 410, outputTokens = 31 })).Content.ReadFromJsonAsync<CloudExecutionCompletionResult>();
            Assert.Equal("observed", observed!.Status);
            Assert.Equal("already_observed", (await (await workerClient.PostAsJsonAsync(meteredObservationPath, new { inputTokens = 410, outputTokens = 31 })).Content.ReadFromJsonAsync<CloudExecutionCompletionResult>())!.Status);
            Assert.Equal(HttpStatusCode.Conflict, (await workerClient.PostAsJsonAsync(meteredObservationPath, new { inputTokens = 411, outputTokens = 30 })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await meteredClient.PostAsJsonAsync(meteredCompletionPath, new { inputTokens = 410, outputTokens = 31 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await workerClient.PostAsync(meteredCompletionPath,
                new StringContent("{\"inputTokens\":1,\"InputTokens\":2,\"outputTokens\":1}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await workerClient.PostAsJsonAsync(meteredCompletionPath, new { inputTokens = 2048, outputTokens = 1 })).StatusCode);
            var meteredCompletions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                workerClient.PostAsJsonAsync(meteredCompletionPath, new { inputTokens = 410, outputTokens = 31 })));
            var meteredStatuses = await Task.WhenAll(meteredCompletions.Select(async response => (await response.Content.ReadFromJsonAsync<CloudExecutionCompletionResult>())!.Status));
            Assert.Single(meteredStatuses, status => status == "completed");
            Assert.Equal(7, meteredStatuses.Count(status => status == "already_completed"));
            Assert.Equal(HttpStatusCode.Conflict, (await workerClient.PostAsJsonAsync(meteredCompletionPath, new { inputTokens = 411, outputTokens = 30 })).StatusCode);
            var failureRequest = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK, (await Inspect(meteredClient, "{\"fields\":[\"v1\",\"safe\",\"route\"]}", failureRequest)).StatusCode);
            var failureReservation = await (await meteredClient.PostAsJsonAsync(dualPath, dualRequest with { requestId = failureRequest })).Content.ReadFromJsonAsync<CloudQuotaResult>();
            Assert.Equal("reserved", failureReservation!.Status);
            var failureClaim = await (await meteredClient.PostAsJsonAsync($"{quotaPath}/{failureReservation.ReservationId!.Value:D}/claim", proof)).Content.ReadFromJsonAsync<CloudExecutionClaimResult>();
            Assert.Equal("claimed", failureClaim!.Status);
            var failurePrefix = $"{quotaPath}/{failureReservation.ReservationId.Value:D}/claims/{failureClaim.ClaimId!.Value:D}";
            using (var message = new HttpRequestMessage(HttpMethod.Post, failurePrefix + "/consume-metered"))
            {
                message.Headers.Add("X-OpenConnect-Worker-Payload-Fingerprint", Convert.ToBase64String(new byte[32]));
                message.Headers.Add("X-OpenConnect-Worker-Context-Tokens", "2048");
                Assert.Equal(HttpStatusCode.OK, (await workerClient.SendAsync(message)).StatusCode);
            }
            var failedPath = failurePrefix + "/fail-metered";
            Assert.Equal(HttpStatusCode.Unauthorized, (await meteredClient.PostAsJsonAsync(failedPath, new { inputTokens = 5, outputTokens = 2 })).StatusCode);
            var failures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => workerClient.PostAsJsonAsync(failedPath, new { inputTokens = 5, outputTokens = 2 })));
            var failureStatuses = await Task.WhenAll(failures.Select(async response => (await response.Content.ReadFromJsonAsync<CloudExecutionCompletionResult>())!.Status));
            Assert.Single(failureStatuses, status => status == "failed");
            Assert.Equal(7, failureStatuses.Count(status => status == "already_failed"));
            Assert.Equal(HttpStatusCode.Conflict, (await workerClient.PostAsJsonAsync(failurePrefix + "/complete-metered", new { inputTokens = 5, outputTokens = 2 })).StatusCode);
            using (var output = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/liveui/output-privacy")
            { Content = new StringContent("{\"fields\":[\"v1\",\"activate\",\"safe\"]}", System.Text.Encoding.UTF8, "application/json") })
            {
                output.Headers.Add("X-Cloud-Request-Id", failureRequest.ToString("D"));
                output.Headers.Add("X-Cloud-Reservation-Id", failureReservation.ReservationId.Value.ToString("D"));
                output.Headers.Add("X-Cloud-Claim-Id", failureClaim.ClaimId.Value.ToString("D"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await meteredClient.SendAsync(output)).StatusCode);
            }
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(448, (await db.QuotaBuckets.SingleAsync(item => item.Id == meteredTokenBucket)).Consumed);
            await VerifyNodeControlPlaneAsync(address, server, gateway, issued.Secret!, first.requestId, reserved.ReservationId.Value, claim.ClaimId.Value, expiry);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var final = await db.QuotaReservations.SingleAsync(x => x.Id == reserved.ReservationId.Value);
                Assert.Equal(1, final.Actual);
                Assert.NotNull(final.FinalizedAt);
                Assert.Equal(1, (await db.QuotaBuckets.SingleAsync(x => x.Id == final.BucketId)).Consumed);
                Assert.Equal(1, (await db.UsageRecords.SingleAsync(x => x.ReservationId == final.Id)).Quantity);
                Assert.Equal(4, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_output_privacy_evidence WHERE \"ReservationId\" = {final.Id}").SingleAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.liveui_output_privacy_evidence WHERE \"ReservationId\" = {final.Id}"));
            }
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, app.Id, issued.Credential!.Id, default));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(accessPath)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Inspect(client, "{\"fields\":[\"v1\",\"safe\",\"route\"]}")).StatusCode);
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.liveui");
            capability.PlatformAvailability = previousAvailability;
            capability.AllowedRegions = previousRegions;
            capability.AllowedRegionsVerified = previousRegionsVerified;
            await db.SaveChangesAsync();
        }
    }

    private static async Task VerifyNodeControlPlaneAsync(Uri address, X509Certificate2 server, X509Certificate2 gateway, string credential,
        Guid requestId, Guid reservationId, Guid claimId, DateTimeOffset expiresAt)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE);const c=m.createLiveUIControlPlaneClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY});const x=JSON.parse(process.env.OC_TEST_DATA);const a=await c.findFingerprintKey(process.env.OC_TEST_CREDENTIAL,x.requestId);const b=await c.reserve(process.env.OC_TEST_CREDENTIAL,{requestId:x.requestId,fingerprintKeyId:'key',fingerprint:Buffer.alloc(32).toString('base64'),expiresAt:x.expiresAt});const v=await c.verifyClaim(process.env.OC_TEST_CREDENTIAL,x.reservationId,x.claimId);const o=await c.inspectOutput(process.env.OC_TEST_CREDENTIAL,x.requestId,x.reservationId,x.claimId,['v1','activate','tasks.tab']);const p=await c.inspectOutput(process.env.OC_TEST_CREDENTIAL,x.requestId,x.reservationId,x.claimId,['v1','person@','example.test']);process.stdout.write(JSON.stringify([a.status,b.status,v,o.code,p.code]));";
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "liveui-control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = gateway.ExportCertificatePem();
        using var privateKey = gateway.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_DATA"] = System.Text.Json.JsonSerializer.Serialize(new { requestId, reservationId, claimId, expiresAt });
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
        Assert.Equal("[\"found\",\"duplicate\",true,\"allowed\",\"privacy_blocked\"]", output);
    }

    private static async Task VerifyNodeDualQuotaAsync(Uri address, X509Certificate2 server, X509Certificate2 gateway, string credential,
        Guid requestId, DateTimeOffset expiresAt)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE);const c=m.createLiveUIControlPlaneClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY});const x=JSON.parse(process.env.OC_TEST_DATA);const q={requestId:x.requestId,fingerprintKeyId:'key',fingerprint:Buffer.alloc(32).toString('base64'),expiresAt:x.expiresAt,tokenAmount:2048};const a=await c.reserveDual(process.env.OC_TEST_CREDENTIAL,q);const b=await c.reserveDual(process.env.OC_TEST_CREDENTIAL,{...q,tokenAmount:2049});process.stdout.write(JSON.stringify([a.status,b.status]));";
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "liveui-control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = gateway.ExportCertificatePem();
        using var privateKey = gateway.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_DATA"] = System.Text.Json.JsonSerializer.Serialize(new { requestId, expiresAt });
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
        Assert.Equal("[\"duplicate\",\"idempotency_conflict\"]", output);
    }

    private static async Task VerifyNodePrivacyAsync(Uri address, X509Certificate2 server, X509Certificate2 gateway, string credential, Guid requestId)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE);const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY});const proof=Buffer.alloc(32).toString('base64');const a=await c.inspectLiveUI(process.env.OC_TEST_CREDENTIAL,process.env.OC_TEST_REQUEST,['v1','show tasks','route.root'],'key',proof);const b=await c.inspectLiveUI(process.env.OC_TEST_CREDENTIAL,process.env.OC_TEST_REQUEST,['v1','person@','example.test'],'key',proof);process.stdout.write(JSON.stringify([a.code,b.code]));";
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = gateway.ExportCertificatePem();
        using var privateKey = gateway.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_REQUEST"] = requestId.ToString("D");
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
        Assert.Equal("[\"allowed\",\"privacy_blocked\"]", output);
    }

    private static X509Certificate2 Certificate(bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client)
        { var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build()); }
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    private static HttpClient Client(Uri address, X509Certificate2 server, X509Certificate2 client)
    {
        var expected = server.GetCertHash(HashAlgorithmName.SHA256);
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256))
        };
        handler.ClientCertificates.Add(client);
        return new HttpClient(handler) { BaseAddress = address };
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
