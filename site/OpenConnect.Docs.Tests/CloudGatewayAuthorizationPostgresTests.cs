using System.Net;
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
public sealed class CloudGatewayAuthorizationPostgresTests
{
    [CloudPostgresFact]
    public async Task DirectTlsAndPinnedClientCertificateProtectCredentialAuthorization()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        string? originalAvailability = null;
        string? originalClassificationAvailability = null;
        string? originalClassificationRegions = null;
        bool originalRegionsVerified = false;
        try
        {
            var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
            var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
            var tenant = await tenants.CreateTenantAsync(owner, "Gateway test", CancellationToken.None);
            var appRecord = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
            var otherApp = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Other app", "Development", CancellationToken.None)).Application!;
            var capability = "inference.chat";
            var classificationReference = "classification-" + Guid.NewGuid().ToString("N");
            await using (var db = await factory.CreateDbContextAsync())
            {
                var definition = await db.Capabilities.SingleAsync(x => x.Id == capability);
                var classification = await db.Capabilities.SingleAsync(x => x.Id == "classification.classify");
                originalAvailability = definition.PlatformAvailability;
                originalClassificationAvailability = classification.PlatformAvailability;
                originalClassificationRegions = classification.AllowedRegions;
                originalRegionsVerified = classification.AllowedRegionsVerified;
                definition.PlatformAvailability = "available";
                classification.PlatformAvailability = "available";
                db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
                db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capability, PlanGranted = true, Revision = 1 });
                db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capability, Enabled = true, Revision = 1 });
                db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = appRecord.Id, CapabilityId = capability, Enabled = true, Revision = 1 });
                db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "classification.classify", PlanGranted = true, Revision = 1 });
                db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
                db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = appRecord.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
                db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = otherApp.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
                db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = Guid.NewGuid(), CapabilityId = "classification.classify", EndpointReference = classificationReference,
                    Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = "model", ModelRevision = "revision-1",
                    ModelDigest = "sha256:" + new string('a', 64), ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
                await db.SaveChangesAsync();
            }
            var catalog = new CloudCatalogRegionsService(new EfCloudCatalogRegionsStore(factory, TimeProvider.System));
            var catalogRevision = (await catalog.GetAsync("classification.classify", CancellationToken.None))!.Revision;
            Assert.Equal("succeeded", (await catalog.PutAsync(new CloudSubject("operator-issuer", "operator"), Guid.NewGuid().ToString("N"), "classification.classify", ["region-a"], true, catalogRevision, CancellationToken.None)).Status);
            var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
            var issued = await credentials.CreateAsync(owner, tenant.Id, appRecord.Id, [capability], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
            Assert.Equal("succeeded", issued.Status);
            var classificationCredential = await credentials.CreateAsync(owner, tenant.Id, appRecord.Id, ["classification.classify"], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
            Assert.Equal("succeeded", classificationCredential.Status);
            var otherCredential = await credentials.CreateAsync(owner, tenant.Id, otherApp.Id, ["classification.classify"], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
            Assert.Equal("succeeded", otherCredential.Status);
            using var serverCertificate = Certificate(client: false);
            using var clientCertificate = Certificate(client: true);
            using var otherCertificate = Certificate(client: true);
            Assert.Throws<InvalidOperationException>(() => new CloudGatewayClientTrust([]));
            Assert.Throws<InvalidOperationException>(() => new CloudGatewayClientTrust(["invalid"]));
            var trust = new CloudGatewayClientTrust([Convert.ToHexString(clientCertificate.GetCertHash(HashAlgorithmName.SHA256))]);
            Assert.True(trust.IsTrusted(clientCertificate, DateTimeOffset.UtcNow));
            Assert.False(trust.IsTrusted(otherCertificate, DateTimeOffset.UtcNow));
            Assert.False(trust.IsTrusted(serverCertificate, DateTimeOffset.UtcNow));
            Assert.False(trust.IsTrusted(clientCertificate, DateTimeOffset.UtcNow.AddDays(2)));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(serverCertificate, https =>
            {
                trust.ConfigureHttps(https);
            })));
            builder.Services.AddSingleton(factory);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>();
            builder.Services.AddScoped<CloudCredentialService>();
            builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>();
            builder.Services.AddScoped<CloudMachineAuthorizationService>();
            builder.Services.AddScoped<ICloudClassificationDeploymentReader, EfCloudClassificationDeploymentReader>();
            builder.Services.AddScoped<CloudClassificationDeploymentService>();
            builder.Services.AddScoped<ICloudQuotaStore, EfCloudQuotaStore>();
            builder.Services.AddScoped<CloudQuotaService>();
            builder.Services.AddScoped<ICloudExecutionClaimStore, EfCloudExecutionClaimStore>();
            builder.Services.AddScoped<CloudExecutionClaimService>();
            builder.Services.AddScoped<ICloudExecutionCompletionStore, EfCloudExecutionCompletionStore>();
            builder.Services.AddScoped<CloudExecutionCompletionService>();
            builder.Services.AddSingleton(new CloudClassificationPrivacyService(new RulesSecurityDetector()));
            var evidenceStore = new SwitchableEvidenceStore(new EfCloudPrivacyEvidenceStore(factory, TimeProvider.System));
            builder.Services.AddSingleton<ICloudPrivacyEvidenceStore>(evidenceStore);
            await using var app = builder.Build();
            app.MapCloudGatewayAuthorization(trust, databaseConfigured: true);
            app.MapCloudClassificationPrivacy(trust, databaseConfigured: true);
            app.MapCloudClassificationDeployment(trust, databaseConfigured: true);
            app.MapCloudFingerprintKeyLookup(trust, databaseConfigured: true);
            app.MapCloudClassificationQuota(trust, databaseConfigured: true);
            app.MapCloudClassificationClaim(trust, databaseConfigured: true);
            app.MapCloudClassificationCompletion(trust, databaseConfigured: true);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var address = new Uri(addresses.Addresses.Single());
            using var anonymous = Client(address, serverCertificate, null);
            anonymous.DefaultRequestHeaders.Add("X-Client-Cert", Convert.ToBase64String(clientCertificate.RawData));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability })).StatusCode);
            using var other = Client(address, serverCertificate, otherCertificate);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => other.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability }));
            using var authorized = Client(address, serverCertificate, clientCertificate);
            var response = await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var result = await response.Content.ReadFromJsonAsync<CloudGatewayAuthorizationResponse>();
            Assert.True(result!.Authorized);
            Assert.Equal(tenant.Id, result.Identity!.TenantId);
            Assert.Equal(appRecord.Id, result.Identity.ApplicationId);
            Assert.True((await NodeAuthorizeAsync(address, serverCertificate, clientCertificate, issued.Secret!, capability)).Authorized);
            var gatewayResult = await NodeGatewayAsync(address, serverCertificate, clientCertificate, issued.Secret!);
            Assert.True(gatewayResult.Checked);
            Assert.Equal(503, gatewayResult.Status);
            Assert.Equal("capability_unavailable", gatewayResult.Code);
            var quotaRequestId = Guid.NewGuid();
            Assert.Equal("not_found", (await NodeFingerprintKeyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, quotaRequestId)).Status);
            var now = DateTimeOffset.UtcNow;
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = appRecord.Id,
                    CapabilityId = "classification.classify", Unit = "request", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(10), Limit = 6 });
                await db.SaveChangesAsync();
            }
            var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
            Assert.Equal("reserved", (await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, appRecord.Id, "classification.classify", "request",
                quotaRequestId, "retained_key", new byte[32], 1, now.AddMinutes(5)), CancellationToken.None)).Status);
            const string reservationPath = "/cloud/internal/v1/classification/quota/reservations";
            var newRequestId = Guid.NewGuid();
            var reservationBody = new { requestId = newRequestId, fingerprintKeyId = "retained_key", fingerprint = Convert.ToBase64String(new byte[32]), expiresAt = now.AddMinutes(4) };
            async Task<HttpResponseMessage> Reserve(HttpClient client, string? secret, object body)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, reservationPath) { Content = JsonContent.Create(body) };
                if (secret is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                return await client.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Reserve(anonymous, classificationCredential.Secret, reservationBody)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Reserve(authorized, issued.Secret, reservationBody)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Reserve(authorized, otherCredential.Secret, reservationBody)).StatusCode);
            var reservation = await Reserve(authorized, classificationCredential.Secret, reservationBody);
            Assert.Equal(HttpStatusCode.OK, reservation.StatusCode);
            var reserved = (await reservation.Content.ReadFromJsonAsync<CloudQuotaResult>())!;
            Assert.Equal("reserved", reserved.Status);
            var duplicate = await Reserve(authorized, classificationCredential.Secret, reservationBody);
            Assert.Equal("duplicate", (await duplicate.Content.ReadFromJsonAsync<CloudQuotaResult>())!.Status);
            Assert.Equal("duplicate", (await NodeReserveAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reservationBody)).Status);
            Assert.Equal("duplicate", (await NodeReserveFromLookupAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, newRequestId)).Status);
            var adapterRequestId = Guid.NewGuid();
            var adapterBody = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "openconnect.cloud.classification/v2", requestId = adapterRequestId,
                capability = "classification.classify", expiresAt = now.AddMinutes(3).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), input = new { text = "test fixture" } });
            var adapterResults = await NodeQuotaAdapterAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, tenant.Id, appRecord.Id, adapterBody);
            Assert.Equal(new[] { "reserved", "duplicate", "idempotency_conflict" }, adapterResults.Select(x => x.Status));
            Assert.Equal(adapterResults[0].ReservationId, adapterResults[1].ReservationId);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var persisted = await db.QuotaReservations.SingleAsync(x => x.TenantId == tenant.Id && x.ApplicationId == appRecord.Id && x.RequestId == adapterRequestId);
                Assert.Equal("old", persisted.FingerprintKeyId);
                var expected = CloudRequestFingerprint.Compute("old", Enumerable.Repeat((byte)2, 32).ToArray(), new CloudFingerprintInput(
                    tenant.Id, appRecord.Id, "classification.classify", "POST", "/cloud/v1/classification", "application/json", System.Text.Encoding.UTF8.GetBytes(adapterBody)));
                Assert.Equal(expected, persisted.RequestFingerprint);
            }
            Assert.Equal("quota_unavailable", (await NodeReserveAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, reservationBody)).Status);
            var conflictingBody = new { requestId = newRequestId, fingerprintKeyId = "retained_key", fingerprint = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), expiresAt = now.AddMinutes(4) };
            Assert.Equal(HttpStatusCode.Conflict, (await Reserve(authorized, classificationCredential.Secret, conflictingBody)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Reserve(authorized, classificationCredential.Secret, new { requestId = Guid.NewGuid(), fingerprintKeyId = "retained_key", fingerprint = "invalid", expiresAt = now.AddMinutes(4) })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Reserve(authorized, classificationCredential.Secret, new { requestId = Guid.NewGuid(), fingerprintKeyId = "retained_key", fingerprint = Convert.ToBase64String(new byte[32]), expiresAt = now.AddHours(1) })).StatusCode);
            var claimPath = $"/cloud/internal/v1/classification/quota/reservations/{reserved.ReservationId:D}/claim";
            async Task<HttpResponseMessage> Claim(HttpClient client, string? secret, HttpContent? body = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, claimPath) { Content = body ?? JsonContent.Create(new { fingerprintKeyId = "retained_key", fingerprint = Convert.ToBase64String(new byte[32]), endpointReference = classificationReference, region = "region-a", contractVersion = "openconnect.v1", modelRevision = "revision-1" }) };
                if (secret is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                return await client.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Claim(anonymous, classificationCredential.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Claim(authorized, issued.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Claim(authorized, otherCredential.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Claim(authorized, classificationCredential.Secret, new StringContent("unexpected"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Claim(authorized, classificationCredential.Secret,
                JsonContent.Create(new { fingerprintKeyId = "retained_key", fingerprint = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), endpointReference = classificationReference, region = "region-a", contractVersion = "openconnect.v1", modelRevision = "revision-1" }))).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-b\"]"));
            Assert.Equal("unavailable", (await NodeClaimAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reserved.ReservationId!.Value, classificationReference)).Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-a\"]"));
                await db.WorkerDeployments.Where(x => x.EndpointReference == classificationReference).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            }
            Assert.Equal("unavailable", (await NodeClaimAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reserved.ReservationId.Value, classificationReference)).Status);
            await using (var db = await factory.CreateDbContextAsync())
                await db.WorkerDeployments.Where(x => x.EndpointReference == classificationReference).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
            Assert.Equal("idempotency_conflict", (await NodeClaimAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reserved.ReservationId!.Value, classificationReference, "wrong_key")).Status);
            CloudExecutionClaimResult claim;
            await using (var locker = await factory.CreateDbContextAsync())
            await using (var regionWriter = await factory.CreateDbContextAsync())
            await using (var deploymentWriter = await factory.CreateDbContextAsync())
            await using (var catalogWriter = await factory.CreateDbContextAsync())
            await using (var observer = await factory.CreateDbContextAsync())
            {
                await using var transaction = await locker.Database.BeginTransactionAsync();
                await locker.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reserved.ReservationId.Value} FOR UPDATE").AsNoTracking().SingleAsync();
                var pendingClaim = NodeClaimAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reserved.ReservationId.Value, classificationReference);
                var waiting = false;
                for (var attempt = 0; attempt < 100 && !waiting; attempt++)
                {
                    waiting = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%cloud.quota_reservations%FOR UPDATE%') AS \"Value\"").SingleAsync();
                    if (!waiting) await Task.Delay(20);
                }
                Assert.True(waiting);
                var pendingRegion = regionWriter.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-b\"]"));
                var pendingDeployment = deploymentWriter.WorkerDeployments.Where(x => x.EndpointReference == classificationReference).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
                var pendingCatalog = catalogWriter.Capabilities.Where(x => x.Id == "classification.classify").ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegionsVerified, false));
                await Task.Delay(100);
                Assert.False(pendingRegion.IsCompleted); Assert.False(pendingDeployment.IsCompleted); Assert.False(pendingCatalog.IsCompleted);
                await transaction.CommitAsync();
                claim = await pendingClaim;
                Assert.Equal(1, await pendingRegion); Assert.Equal(1, await pendingDeployment);
                Assert.Equal(1, await pendingCatalog);
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-a\"]"));
                await db.WorkerDeployments.Where(x => x.EndpointReference == classificationReference).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
                await db.Capabilities.Where(x => x.Id == "classification.classify").ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegionsVerified, true));
            }
            Assert.Equal("claimed", claim.Status);
            Assert.NotNull(claim.ClaimId);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var storedClaim = await db.ExecutionClaims.SingleAsync(x => x.ReservationId == reserved.ReservationId.Value);
                Assert.Equal("region-a", storedClaim.Region);
                Assert.Equal((await db.WorkerDeployments.SingleAsync(x => x.EndpointReference == classificationReference)).Id, storedClaim.DeploymentId);
            }
            Assert.Equal("already_claimed", (await NodeClaimAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reserved.ReservationId.Value, classificationReference)).Status);
            Assert.Equal("unavailable", (await NodeClaimAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, reserved.ReservationId.Value, classificationReference)).Status);
            Assert.Equal("execution_requires_reconciliation", (await quota.FinalizeAsync(tenant.Id, appRecord.Id, reserved.ReservationId.Value, 0, CancellationToken.None)).Status);
            var completions = new CloudExecutionCompletionService(new EfCloudExecutionCompletionStore(factory, TimeProvider.System));
            Assert.Equal("unavailable", (await completions.CompleteClassificationAsync(tenant.Id, otherApp.Id, reserved.ReservationId.Value, claim.ClaimId!.Value, "completed", CancellationToken.None)).Status);
            Assert.Equal("unavailable", (await completions.CompleteClassificationAsync(tenant.Id, appRecord.Id, reserved.ReservationId.Value, Guid.NewGuid(), "completed", CancellationToken.None)).Status);
            Assert.Equal("invalid_request", (await completions.CompleteClassificationAsync(tenant.Id, appRecord.Id, reserved.ReservationId.Value, claim.ClaimId.Value, "unknown", CancellationToken.None)).Status);
            var completed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => completions.CompleteClassificationAsync(tenant.Id, appRecord.Id, reserved.ReservationId.Value, claim.ClaimId.Value, "completed", CancellationToken.None)));
            Assert.Single(completed, x => x.Status == "completed");
            Assert.Equal(7, completed.Count(x => x.Status == "already_completed"));
            Assert.Equal("already_completed", (await NodeCompleteAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, reserved.ReservationId.Value, claim.ClaimId.Value)).Status);
            Assert.Equal("unavailable", (await NodeCompleteAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, reserved.ReservationId.Value, claim.ClaimId.Value)).Status);
            Assert.Equal("invalid_request", (await completions.CompleteClassificationAsync(tenant.Id, appRecord.Id, reserved.ReservationId.Value, claim.ClaimId.Value, "failed", CancellationToken.None)).Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var ledger = await db.UsageRecords.SingleAsync(x => x.ReservationId == reserved.ReservationId.Value);
                Assert.Equal(1, ledger.Quantity);
                Assert.Equal("classification.classify", ledger.CapabilityId);
                Assert.Equal("request", ledger.Unit);
                Assert.Equal("completed", (await db.ExecutionCompletions.SingleAsync(x => x.ReservationId == reserved.ReservationId.Value)).Outcome);
                await Assert.ThrowsAnyAsync<Exception>(() => db.ExecutionCompletions.Where(x => x.ReservationId == reserved.ReservationId.Value).ExecuteDeleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.execution_completions"));
            }
            var failedReservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, appRecord.Id, "classification.classify", "request", Guid.NewGuid(), "retained_key", new byte[32], 1, now.AddMinutes(5)), CancellationToken.None);
            var failedClaim = await new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, TimeProvider.System)).ClaimAsync(classificationCredential.Credential!.Id, tenant.Id, appRecord.Id, failedReservation.ReservationId!.Value, "classification.classify", "retained_key", new byte[32], new CloudClassificationDeploymentMatchRequest(classificationReference, "region-a", "openconnect.v1", "revision-1"), CancellationToken.None);
            Assert.Equal("claimed", failedClaim.Status);
            Assert.Equal("invalid_request", (await completions.CompleteClassificationAsync(tenant.Id, appRecord.Id, failedReservation.ReservationId.Value, failedClaim.ClaimId!.Value, "failed", CancellationToken.None)).Status);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.False(await db.ExecutionCompletions.AnyAsync(x => x.ReservationId == failedReservation.ReservationId.Value));
                Assert.False(await db.UsageRecords.AnyAsync(x => x.ReservationId == failedReservation.ReservationId.Value));
                Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == failedReservation.ReservationId.Value)).FinalizedAt);
            }
            var nodeReservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, appRecord.Id, "classification.classify", "request", Guid.NewGuid(), "retained_key", new byte[32], 1, now.AddMinutes(5)), CancellationToken.None);
            Assert.Equal("reserved", nodeReservation.Status);
            var nodeClaim = await NodeClaimAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, nodeReservation.ReservationId!.Value, classificationReference);
            Assert.Equal("claimed", nodeClaim.Status);
            var completionPath = $"/cloud/internal/v1/classification/quota/reservations/{nodeReservation.ReservationId:D}/claims/{nodeClaim.ClaimId:D}/completion";
            async Task<HttpResponseMessage> Complete(HttpClient client, string? secret, HttpContent? body = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, completionPath) { Content = body };
                if (secret is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                return await client.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(anonymous, classificationCredential.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Complete(authorized, issued.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Complete(authorized, classificationCredential.Secret, new StringContent("unexpected"))).StatusCode);
            Assert.Equal("unavailable", (await NodeCompleteAsync(address, serverCertificate, clientCertificate, otherCredential.Secret!, nodeReservation.ReservationId.Value, nodeClaim.ClaimId!.Value)).Status);
            await using (var db = await factory.CreateDbContextAsync())
                await db.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == appRecord.Id && x.CapabilityId == "classification.classify").ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            Assert.Equal("completed", (await NodeCompleteAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, nodeReservation.ReservationId.Value, nodeClaim.ClaimId.Value)).Status);
            Assert.Equal("already_completed", (await NodeCompleteAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, nodeReservation.ReservationId.Value, nodeClaim.ClaimId.Value)).Status);
            await using (var db = await factory.CreateDbContextAsync())
                await db.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == appRecord.Id && x.CapabilityId == "classification.classify").ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
            var keyLookup = await NodeFingerprintKeyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, quotaRequestId);
            Assert.Equal("found", keyLookup.Status);
            Assert.Equal("retained_key", keyLookup.KeyId);
            Assert.Equal(now.AddMinutes(5).Ticks / 10, keyLookup.ExpiresAt!.Value.Ticks / 10);
            Assert.Equal("not_found", (await NodeFingerprintKeyAsync(address, serverCertificate, clientCertificate, otherCredential.Secret!, quotaRequestId)).Status);
            Assert.Equal("unavailable", (await NodeFingerprintKeyAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, quotaRequestId)).Status);
            Assert.Equal("unavailable", (await NodeFingerprintKeyAsync(address, serverCertificate, clientCertificate, issued.Secret!, quotaRequestId)).Status);
            var legacyRequestId = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var bucket = await db.QuotaBuckets.SingleAsync(x => x.TenantId == tenant.Id && x.ApplicationId == appRecord.Id && x.CapabilityId == "classification.classify");
                bucket.Reserved++;
                db.QuotaReservations.Add(new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = bucket.Id, TenantId = tenant.Id,
                    ApplicationId = appRecord.Id, RequestId = legacyRequestId, Amount = 1, ExpiresAt = now.AddMinutes(5) });
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            Assert.Equal("legacy_unverifiable", (await NodeFingerprintKeyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, legacyRequestId)).Status);
            Assert.Equal("allowed", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world")).Code);
            Assert.Equal("privacy_blocked", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "email person@example.com")).Code);
            Assert.Equal("capability_unavailable", (await NodePrivacyAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, "hello world")).Code);
            using (var privacyRequest = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/classification/privacy"))
            {
                privacyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", classificationCredential.Secret);
                privacyRequest.Headers.Add("X-Cloud-Request-Id", Guid.NewGuid().ToString("D"));
                privacyRequest.Content = new StringContent(new string('x', 65537), System.Text.Encoding.UTF8, "text/plain");
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await authorized.SendAsync(privacyRequest)).StatusCode);
            }
            Assert.True(await NodeMatchAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, classificationReference));
            await using (var db = await factory.CreateDbContextAsync())
                await db.Capabilities.Where(x => x.Id == "classification.classify").ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegionsVerified, false));
            Assert.False(await NodeMatchAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, classificationReference));
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Capabilities.Where(x => x.Id == "classification.classify").ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegionsVerified, true));
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-b\"]"));
            }
            Assert.False(await NodeMatchAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, classificationReference));
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-a\"]"));
            Assert.False(await NodeMatchAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "unknown-deployment"));
            Assert.False(await NodeMatchAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, classificationReference));
            Assert.False(await NodeMatchAsync(address, serverCertificate, clientCertificate, issued.Secret!, classificationReference));
            using (var invalidMatch = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/classification/deployment-match"))
            {
                invalidMatch.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", classificationCredential.Secret);
                invalidMatch.Content = new StringContent("{\"region\":\"region-a\",\"Region\":\"region-b\"}", System.Text.Encoding.UTF8, "application/json");
                Assert.Equal(HttpStatusCode.BadRequest, (await authorized.SendAsync(invalidMatch)).StatusCode);
            }
            var deploymentService = new CloudClassificationDeploymentService(new EfCloudClassificationDeploymentReader(factory));
            var match = new CloudClassificationDeploymentMatchRequest(classificationReference, "region-a", "openconnect.v1", "revision-1");
            Assert.True(await deploymentService.MatchesAsync(tenant.Id, match, CancellationToken.None));
            Assert.False(await deploymentService.MatchesAsync(Guid.NewGuid(), match, CancellationToken.None));
            Assert.False(await deploymentService.MatchesAsync(tenant.Id, match with { Region = "region-b" }, CancellationToken.None));
            Assert.False(await deploymentService.MatchesAsync(tenant.Id, match with { ContractVersion = "other" }, CancellationToken.None));
            Assert.False(await deploymentService.MatchesAsync(tenant.Id, match with { ModelRevision = "other" }, CancellationToken.None));
            var blocked = await NodeClassificationGatewayAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "email person@example.com", classificationReference);
            Assert.Equal(403, blocked.Status);
            Assert.Equal("privacy_blocked", blocked.Code);
            var clean = await NodeClassificationGatewayAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world", classificationReference);
            Assert.Equal(503, clean.Status);
            Assert.Equal("capability_unavailable", clean.Code);
            await using (var verify = await factory.CreateDbContextAsync())
            {
                var evidence = await verify.PrivacyEvidence.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ApplicationId == appRecord.Id).ToListAsync();
                Assert.Equal(2, evidence.Count(x => x.Outcome == "allowed"));
                Assert.Equal(2, evidence.Count(x => x.Outcome == "privacy_blocked"));
                Assert.Contains(evidence, x => x.Outcome == "privacy_blocked" && x.TraceId == blocked.TraceId);
                Assert.Contains(evidence, x => x.Outcome == "allowed" && x.TraceId == clean.TraceId);
                Assert.All(evidence, x => { Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), x.RequestId); Assert.Matches("^[0-9a-f]{32}$", x.TraceId); });
                Assert.DoesNotContain("person@example.com", System.Text.Json.JsonSerializer.Serialize(evidence));
                await Assert.ThrowsAnyAsync<Exception>(() => verify.PrivacyEvidence.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => verify.PrivacyEvidence.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Outcome, "allowed")));
                await Assert.ThrowsAnyAsync<Exception>(() => verify.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.privacy_evidence"));
            }
            evidenceStore.Fail = true;
            Assert.Equal("capability_unavailable", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world")).Code);
            evidenceStore.Fail = false;
            await using (var db = await factory.CreateDbContextAsync())
                await db.WorkerDeployments.Where(x => x.EndpointReference == classificationReference).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            Assert.False(await NodeMatchAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, classificationReference));
            var disabled = await NodeClassificationGatewayAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "email person@example.com", classificationReference);
            Assert.Equal(503, disabled.Status);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.False(await db.PrivacyEvidence.AnyAsync(x => x.TraceId == disabled.TraceId));
            var foreign = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), appRecord.Id, "Development", new HashSet<string>());
            await Assert.ThrowsAsync<DbUpdateException>(() => evidenceStore.AppendAsync(foreign, Guid.NewGuid(), "allowed", Guid.NewGuid().ToString("N"), CancellationToken.None));
            Assert.False((await NodeAuthorizeAsync(address, serverCertificate, otherCertificate, issued.Secret!, capability)).Authorized);
            Assert.False((await NodeAuthorizeAsync(address, otherCertificate, clientCertificate, issued.Secret!, capability)).Authorized);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = new string('x', 5000), capability })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability, tenantId = Guid.NewGuid() })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability = "other" })).StatusCode);
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, appRecord.Id, issued.Credential!.Id, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Unauthorized, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability })).StatusCode);
            Assert.Equal("unauthorized", (await NodeAuthorizeAsync(address, serverCertificate, clientCertificate, issued.Secret!, capability)).Code);
            var revokedGatewayResult = await NodeGatewayAsync(address, serverCertificate, clientCertificate, issued.Secret!);
            Assert.False(revokedGatewayResult.Checked);
            Assert.Equal(401, revokedGatewayResult.Status);
            Assert.Equal("unauthorized", revokedGatewayResult.Code);
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, appRecord.Id, classificationCredential.Credential!.Id, CancellationToken.None));
            Assert.Equal("capability_unavailable", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world")).Code);
            Assert.Equal("unavailable", (await NodeFingerprintKeyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, quotaRequestId)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Reserve(authorized, classificationCredential.Secret, reservationBody)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Claim(authorized, classificationCredential.Secret)).StatusCode);
            Assert.Equal("unavailable", (await NodeCompleteAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, nodeReservation.ReservationId.Value, nodeClaim.ClaimId.Value)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Complete(authorized, classificationCredential.Secret)).StatusCode);
        }
        finally
        {
            if (originalAvailability is not null)
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Capabilities.Where(x => x.Id == "inference.chat")
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, originalAvailability));
            }
            if (originalClassificationAvailability is not null)
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Capabilities.Where(x => x.Id == "classification.classify")
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, originalClassificationAvailability)
                        .SetProperty(y => y.AllowedRegions, originalClassificationRegions!).SetProperty(y => y.AllowedRegionsVerified, originalRegionsVerified));
            }
        }
    }

    private static async Task<CloudFingerprintKeyLookup> NodeFingerprintKeyAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, Guid requestId)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); process.stdout.write(JSON.stringify(await c.findClassificationFingerprintKey(process.env.OC_TEST_CREDENTIAL,process.env.OC_TEST_CAPABILITY)));";
        var output = await NodeCallAsync(script, address, server, client, credential, requestId.ToString("D"));
        return System.Text.Json.JsonSerializer.Deserialize<CloudFingerprintKeyLookup>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudQuotaResult> NodeReserveAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, object body)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); process.stdout.write(JSON.stringify(await c.reserveClassification(process.env.OC_TEST_CREDENTIAL,JSON.parse(process.env.OC_TEST_CAPABILITY))));";
        var output = await NodeCallAsync(script, address, server, client, credential, System.Text.Json.JsonSerializer.Serialize(body));
        return System.Text.Json.JsonSerializer.Deserialize<CloudQuotaResult>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudQuotaResult[]> NodeQuotaAdapterAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, Guid tenantId, Guid applicationId, string body)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const q=await import(new URL('./classification-quota.mjs',process.env.OC_TEST_MODULE)); const p=await import(new URL('./classification-request.mjs',process.env.OC_TEST_MODULE)); const f=await import(new URL('./request-fingerprint.mjs',process.env.OC_TEST_MODULE)); " +
            "const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); const x=JSON.parse(process.env.OC_TEST_CAPABILITY); " +
            "async function run(raw,activeKeyId){const ring=f.createFingerprintKeyring({activeKeyId,keys:[{id:'old',key:Buffer.alloc(32,2)},{id:'new',key:Buffer.alloc(32,1)}]});try {const r={headers:new Headers({'content-type':'application/json'}),readBody:async()=>({bytes:Buffer.from(raw)})}; return await p.withClassificationRequest(r,(value,body)=>q.reserveClassificationRequest({credential:process.env.OC_TEST_CREDENTIAL,identity:x.identity,value,body},{controlPlane:c,keyring:ring}));} finally {ring.dispose();}} " +
            "process.stdout.write(JSON.stringify([await run(x.body,'old'),await run(x.body,'new'),await run(x.body+' ','new')]));";
        var input = System.Text.Json.JsonSerializer.Serialize(new { identity = new { tenantId, applicationId }, body });
        var output = await NodeCallAsync(script, address, server, client, credential, input);
        return System.Text.Json.JsonSerializer.Deserialize<CloudQuotaResult[]>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudExecutionClaimResult> NodeClaimAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, Guid reservationId, string reference, string keyId = "retained_key")
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); const x=JSON.parse(process.env.OC_TEST_DEPLOYMENT); process.stdout.write(JSON.stringify(await c.claimClassification(process.env.OC_TEST_CREDENTIAL,process.env.OC_TEST_CAPABILITY,{fingerprintKeyId:x.keyId,fingerprint:Buffer.alloc(32).toString('base64'),deployment:{endpointReference:x.reference,region:'region-a',contractVersion:'openconnect.v1',modelRevision:'revision-1'}})));";
        var output = await NodeCallAsync(script, address, server, client, credential, reservationId.ToString("D"), System.Text.Json.JsonSerializer.Serialize(new { keyId, reference }));
        return System.Text.Json.JsonSerializer.Deserialize<CloudExecutionClaimResult>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudExecutionCompletionResult> NodeCompleteAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, Guid reservationId, Guid claimId)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); process.stdout.write(JSON.stringify(await c.completeClassification(process.env.OC_TEST_CREDENTIAL,process.env.OC_TEST_CAPABILITY,process.env.OC_TEST_DEPLOYMENT)));";
        var output = await NodeCallAsync(script, address, server, client, credential, reservationId.ToString("D"), claimId.ToString("D"));
        return System.Text.Json.JsonSerializer.Deserialize<CloudExecutionCompletionResult>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudQuotaResult> NodeReserveFromLookupAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, Guid requestId)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); const requestId=process.env.OC_TEST_CAPABILITY; const k=await c.findClassificationFingerprintKey(process.env.OC_TEST_CREDENTIAL,requestId); process.stdout.write(JSON.stringify(await c.reserveClassification(process.env.OC_TEST_CREDENTIAL,{requestId,fingerprintKeyId:k.keyId,expiresAt:k.expiresAt,fingerprint:Buffer.alloc(32).toString('base64')})));";
        var output = await NodeCallAsync(script, address, server, client, credential, requestId.ToString("D"));
        return System.Text.Json.JsonSerializer.Deserialize<CloudQuotaResult>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private sealed class SwitchableEvidenceStore(ICloudPrivacyEvidenceStore inner) : ICloudPrivacyEvidenceStore
    {
        public bool Fail { get; set; }
        public Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string outcome, string traceId, CancellationToken cancellationToken) =>
            Fail ? Task.FromException(new InvalidOperationException("test evidence unavailable")) : inner.AppendAsync(identity, requestId, outcome, traceId, cancellationToken);
    }

    private static async Task<CloudPrivacyDecision> NodePrivacyAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string content)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); process.stdout.write(JSON.stringify(await c.inspectClassification(process.env.OC_TEST_CREDENTIAL,'11111111-1111-1111-1111-111111111111',process.env.OC_TEST_CAPABILITY)));";
        var output = await NodeCallAsync(script, address, server, client, credential, content);
        return System.Text.Json.JsonSerializer.Deserialize<CloudPrivacyDecision>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<bool> NodeMatchAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string reference)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); process.stdout.write(JSON.stringify(await c.matchClassificationDeployment(process.env.OC_TEST_CREDENTIAL,{endpointReference:process.env.OC_TEST_CAPABILITY,region:'region-a',contractVersion:'openconnect.v1',modelRevision:'revision-1'})));";
        return bool.Parse(await NodeCallAsync(script, address, server, client, credential, reference));
    }

    private static async Task<GatewayResult> NodeClassificationGatewayAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string content, string reference)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const g=await import(process.env.OC_TEST_GATEWAY); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); const s=g.createLocalServer(c,{deployment:{endpointReference:process.env.OC_TEST_DEPLOYMENT,region:'region-a',contractVersion:'openconnect.v1',modelRevision:'revision-1'},probe:async()=>({ready:true})}); await new Promise(r=>s.listen(0,'127.0.0.1',r)); const trace=crypto.randomUUID().replaceAll('-',''); const x=await fetch('http://127.0.0.1:'+s.address().port+'/cloud/v1/classification',{method:'POST',headers:{authorization:'Bearer '+process.env.OC_TEST_CREDENTIAL,'content-type':'application/json',traceparent:'00-'+trace+'-0123456789abcdef-01'},body:JSON.stringify({schemaVersion:'openconnect.cloud.classification/v2',requestId:'11111111-1111-1111-1111-111111111111',capability:'classification.classify',expiresAt:new Date(Date.now()+60000).toISOString(),input:{text:process.env.OC_TEST_CAPABILITY}})}); const b=await x.json(); await new Promise(r=>s.close(r)); if(b.traceId!==trace) throw new Error('trace mismatch'); process.stdout.write(JSON.stringify({status:x.status,checked:true,code:b.code,traceId:b.traceId}));";
        var output = await NodeCallAsync(script, address, server, client, credential, content, reference);
        return System.Text.Json.JsonSerializer.Deserialize<GatewayResult>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudGatewayAuthorizationResponse> NodeAuthorizeAsync(
        Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string capability)
    {
        var script = "const m = await import(process.env.OC_TEST_MODULE); " +
            "const c = m.createCloudAuthorizationClient({origin: process.env.OC_TEST_ORIGIN, ca: process.env.OC_TEST_CA, certificate: process.env.OC_TEST_CERT, privateKey: process.env.OC_TEST_KEY}); " +
            "process.stdout.write(JSON.stringify(await c.authorize(process.env.OC_TEST_CREDENTIAL, process.env.OC_TEST_CAPABILITY)));";
        var output = await NodeCallAsync(script, address, server, client, credential, capability);
        return System.Text.Json.JsonSerializer.Deserialize<CloudGatewayAuthorizationResponse>(output,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private sealed record GatewayResult(int Status, bool Checked, string Code, string? TraceId = null);

    private static async Task<GatewayResult> NodeGatewayAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const g=await import(process.env.OC_TEST_GATEWAY); " +
            "const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); " +
            "let checked=false; const s=g.createLocalServer({authorize:async (...a)=>{const r=await c.authorize(...a); checked=r.authorized; return r;}}); " +
            "await new Promise(r=>s.listen(0,'127.0.0.1',r)); const x=await fetch('http://127.0.0.1:'+s.address().port+'/cloud/v1/inference/chat',{method:'POST',headers:{authorization:'Bearer '+process.env.OC_TEST_CREDENTIAL},body:'private-test-body'}); " +
            "const b=await x.json(); await new Promise(r=>s.close(r)); process.stdout.write(JSON.stringify({status:x.status,checked,code:b.code}));";
        var output = await NodeCallAsync(script, address, server, client, credential, "inference.chat");
        return System.Text.Json.JsonSerializer.Deserialize<GatewayResult>(output,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<string> NodeCallAsync(string script, Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string capability, string? deployment = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_GATEWAY"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "local-server.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = client.ExportCertificatePem();
        using var privateKey = client.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_CAPABILITY"] = capability;
        if (deployment is not null) start.Environment["OC_TEST_DEPLOYMENT"] = deployment;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
        return output;
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

    private static HttpClient Client(Uri address, X509Certificate2 server, X509Certificate2? client)
    {
        var expected = server.GetCertHash(HashAlgorithmName.SHA256);
        var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256)) };
        if (client is not null) handler.ClientCertificates.Add(client);
        return new HttpClient(handler) { BaseAddress = address };
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
