using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudVoiceClaimPostgresTests
{
    [CloudPostgresFact]
    public async Task ClaimsRequireMatchingInspectionAndRespectDeploymentCapacity()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Voice claim", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Voice app", "Development", default)).Application!;
        var material = CloudCredentialSecrets.Create("Development");
        var deploymentId = Guid.NewGuid(); var endpoint = "voice-" + Guid.NewGuid().ToString("N");
        var digest = "sha256:" + new string('a', 64); var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "voice.synthesize");
            capability.PlatformAvailability = "available"; capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "voice.synthesize", EndpointReference = endpoint,
                Transport = "https-mtls", Region = "region-a", ContractVersion = "v1", ModelId = "kokoro", ModelRevision = "revision-1",
                ModelDigest = digest, ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "voice.synthesize", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "voice.synthesize", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "voice.synthesize", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "voice.synthesize",
                Unit = "character", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = 20 });
            db.Credentials.Add(new CloudCredential { Id = material.Id, TenantId = tenant.Id, ApplicationId = app.Id, Environment = "Development",
                KeyHash = material.Hash, Fingerprint = material.Fingerprint, Scopes = "[\"voice.synthesize\"]", IssuedAt = now.AddMinutes(-1), ExpiresAt = now.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var identity = new CloudCredentialIdentity(material.Id, tenant.Id, app.Id, "Development", new HashSet<string> { "voice.synthesize" });
        var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var evidence = new EfCloudVoicePrivacyEvidenceStore(factory, TimeProvider.System);
        var claim = new CloudVoiceClaimService(new EfCloudVoiceClaimStore(factory, TimeProvider.System));
        var fingerprint = new byte[32]; var workerHash = Enumerable.Repeat((byte)7, 32).ToArray();
        var deployment = new CloudInferenceDeploymentMatch(endpoint, "region-a", "v1", "revision-1", digest);
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        async Task<Guid> Reserve(Guid id) => (await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "voice.synthesize", "character", id,
            "key", fingerprint, 4, now.AddMinutes(1)), default)).ReservationId!.Value;
        var first = await Reserve(firstId); var second = await Reserve(secondId);
        Task<CloudExecutionClaimResult> Claim(Guid id) => claim.ClaimAsync(identity, id, "key", fingerprint, workerHash, "kokoro", deployment, default);
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 5, "allowed", new string('a', 32), default);
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        await evidence.AppendAsync(identity, secondId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var revoked = await db.Credentials.SingleAsync(x => x.Id == material.Id);
            revoked.RevokedAt = now;
            await db.SaveChangesAsync();
        }
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var restored = await db.Credentials.SingleAsync(x => x.Id == material.Id);
            restored.RevokedAt = null;
            await db.SaveChangesAsync();
        }
        await evidence.AppendAsync(identity, secondId, "key", fingerprint, 4, "privacy_blocked", new string('a', 32), default);
        Assert.Equal("unavailable", (await Claim(second)).Status);
        await evidence.AppendAsync(identity, secondId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Claim(first)));
        Assert.Single(attempts, x => x.Status == "claimed");
        Assert.Equal(7, attempts.Count(x => x.Status == "already_claimed"));
        var claimId = attempts.Single(x => x.Status == "claimed").ClaimId!.Value;
        var consumption = new CloudVoiceConsumptionService(new EfCloudVoiceConsumptionStore(factory, TimeProvider.System));
        var settlement = new CloudVoiceSettlement(new EfCloudVoiceSettlementStore(factory, TimeProvider.System));
        var access = new EfCloudVoiceAccessStore(factory, TimeProvider.System);
        Assert.True(await access.IsClaimAllowedAsync(identity, first, claimId, default));
        Assert.False(await access.IsClaimAllowedAsync(identity with { CredentialId = Guid.NewGuid() }, first, claimId, default));
        Assert.False(await access.IsClaimAllowedAsync(identity with { TenantId = Guid.NewGuid() }, first, claimId, default));
        Assert.Equal("unavailable", await settlement.SettleAsync(first, claimId, deploymentId, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = first, ClaimId = claimId, Outcome = "completed", CompletedAt = now });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        Task<string> Consume(Guid worker, byte[] proof) => consumption.ConsumeAsync(first, claimId, worker, proof, default);
        Assert.Equal("unavailable", await Consume(Guid.NewGuid(), workerHash));
        Assert.Equal("unavailable", await Consume(deploymentId, new byte[32]));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var revoked = await db.Credentials.SingleAsync(x => x.Id == material.Id);
            revoked.RevokedAt = now;
            await db.SaveChangesAsync();
        }
        Assert.Equal("unavailable", await Consume(deploymentId, workerHash));
        Assert.False(await access.IsClaimAllowedAsync(identity, first, claimId, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var restored = await db.Credentials.SingleAsync(x => x.Id == material.Id);
            restored.RevokedAt = null;
            await db.SaveChangesAsync();
        }
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 4, "privacy_blocked", new string('a', 32), default);
        Assert.Equal("unavailable", await Consume(deploymentId, workerHash));
        Assert.False(await access.IsClaimAllowedAsync(identity, first, claimId, default));
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var changed = await db.WorkerDeployments.SingleAsync(x => x.Id == deploymentId);
            changed.ModelRevision = "different";
            await db.SaveChangesAsync();
        }
        Assert.Equal("unavailable", await Consume(deploymentId, workerHash));
        Assert.False(await access.IsClaimAllowedAsync(identity, first, claimId, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var restored = await db.WorkerDeployments.SingleAsync(x => x.Id == deploymentId);
            restored.ModelRevision = "revision-1";
            await db.SaveChangesAsync();
        }
        await ConsumeThroughTlsAsync(factory, first, claimId, deploymentId, workerHash);
        await using (var db = await factory.CreateDbContextAsync())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = first, ClaimId = claimId, Outcome = "completed", CompletedAt = now });
            await db.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => db.QuotaReservations.Where(x => x.Id == first)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 3L).SetProperty(y => y.FinalizedAt, now)));
        }
        Assert.Equal("unavailable", (await Claim(second)).Status);
        Assert.Equal("idempotency_conflict", (await claim.ClaimAsync(identity, second, "key", Enumerable.Repeat((byte)1, 32).ToArray(),
            workerHash, "kokoro", deployment, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = second, ClaimId = Guid.NewGuid(), TenantId = tenant.Id,
                ApplicationId = app.Id, DeploymentId = deploymentId, Region = "region-a", WorkerPayloadFingerprint = workerHash, ClaimedAt = now });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var script = db.GetService<IMigrator>().GenerateScript("20261123000100_AddVoicePrivacyEvidence",
                "20261123000300_GuardVoiceSettlement", MigrationsSqlGenerationOptions.Idempotent);
            await db.Database.ExecuteSqlRawAsync(script);
        }
        Assert.Equal("unavailable", await settlement.SettleAsync(first, claimId, Guid.NewGuid(), default));
        var settlements = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => settlement.SettleAsync(first, claimId, deploymentId, default)));
        Assert.Single(settlements, x => x == "completed");
        Assert.Equal(7, settlements.Count(x => x == "already_completed"));
        Assert.True(await access.IsClaimAllowedAsync(identity, first, claimId, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var bucket = await db.QuotaBuckets.SingleAsync(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.Unit == "character");
            Assert.Equal(4, bucket.Reserved);
            Assert.Equal(4, bucket.Consumed);
            Assert.Single(await db.UsageRecords.Where(x => x.ReservationId == first).ToArrayAsync());
        }
        Assert.Equal("claimed", (await Claim(second)).Status);
    }

    private static async Task ConsumeThroughTlsAsync(IDbContextFactory<CloudControlPlaneDbContext> factory,
        Guid reservationId, Guid claimId, Guid deploymentId, byte[] fingerprint)
    {
        using var server = Certificate(false); using var worker = Certificate(true); using var other = Certificate(true);
        var trust = new CloudWorkerClientTrust([new(deploymentId, Convert.ToHexString(worker.GetCertHash(HashAlgorithmName.SHA256)))], null);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(server, https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (_, _, _) => true;
        })));
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ICloudVoiceConsumptionStore, EfCloudVoiceConsumptionStore>();
        builder.Services.AddScoped<CloudVoiceConsumptionService>();
        await using var host = builder.Build();
        host.MapCloudVoiceConsumption(trust, true);
        await host.StartAsync();
        var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var client = Client(address, server, worker); using var untrusted = Client(address, server, other);
        var path = $"/cloud/internal/v1/voice-synthesis/quota/reservations/{reservationId:D}/claims/{claimId:D}/consume";
        async Task<HttpResponseMessage> Send(HttpClient sender)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path);
            message.Headers.Add("X-OpenConnect-Worker-Payload-Fingerprint", Convert.ToBase64String(fingerprint));
            return await sender.SendAsync(message);
        }
        using (var denied = await Send(untrusted)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(client)));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal("{\"status\":\"consumed\"}", await responses.Single(x => x.StatusCode == HttpStatusCode.OK).Content.ReadAsStringAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static X509Certificate2 Certificate(bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client) { var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build()); }
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
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
