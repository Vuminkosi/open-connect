using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoiceQuotaEndpointTests
{
    [Fact]
    public async Task RequiresPinnedIdentityAndReservesOnlyBoundedCharactersForItsScope()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=voice-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var trust = new CloudGatewayClientTrust([Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))]);
        var material = CloudCredentialSecrets.Create("Development");
        var tenant = Guid.NewGuid(); var application = Guid.NewGuid();
        var credential = new CloudCredential { Id = material.Id, TenantId = tenant, ApplicationId = application,
            Environment = "Development", KeyHash = material.Hash, Fingerprint = material.Fingerprint,
            Scopes = "[\"voice.synthesize\"]", IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        var snapshot = new CloudCapabilitySnapshot(
            [new CloudCapabilityDefinition { Id = "voice.synthesize", Name = "Voice", Description = "fixture", ContractVersion = "v1",
                BillingUnit = "character", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" }],
            new Dictionary<string, CloudTenantEntitlement> { ["voice.synthesize"] = new() { TenantId = tenant, CapabilityId = "voice.synthesize", PlanGranted = true } },
            new Dictionary<string, CloudTenantCapabilitySelection> { ["voice.synthesize"] = new() { TenantId = tenant, CapabilityId = "voice.synthesize", Enabled = true } },
            new Dictionary<string, CloudApplicationCapabilitySelection> { ["voice.synthesize"] = new() { TenantId = tenant, ApplicationId = application, CapabilityId = "voice.synthesize", Enabled = true } });
        var quotas = new QuotaStore();
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System))
            .AddSingleton<ICloudMachineCapabilityReader>(new CapabilityReader(snapshot))
            .AddSingleton<CloudMachineAuthorizationService>().AddSingleton<ICloudQuotaStore>(quotas)
            .AddSingleton<CloudQuotaService>().BuildServiceProvider();
        var id = Guid.NewGuid();
        string Json(int characters) => System.Text.Json.JsonSerializer.Serialize(new { requestId = id, fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]), characters, expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        DefaultHttpContext Context(string body)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https"; context.Connection.ClientCertificate = certificate;
            context.Request.Headers.Authorization = "Bearer " + material.Secret;
            context.Request.ContentType = "application/json"; context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return context;
        }
        var noCert = Context(Json(4)); noCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(noCert, trust, true, CancellationToken.None)));
        Assert.Equal(400, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(Context(Json(0)), trust, true, CancellationToken.None)));
        Assert.Equal(400, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(Context(Json(2001)), trust, true, CancellationToken.None)));
        var duplicate = Context(Json(4).Replace("{", "{\"characters\":4,", StringComparison.Ordinal));
        Assert.Equal(400, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(duplicate, trust, true, CancellationToken.None)));
        Assert.Equal(0, quotas.Calls);
        var accepted = Context(Json(4));
        Assert.Equal(200, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(accepted, trust, true, CancellationToken.None)));
        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.Equal((tenant, application, "voice.synthesize", "character", 4L),
            (quotas.Last!.TenantId, quotas.Last.ApplicationId, quotas.Last.CapabilityId, quotas.Last.Unit, quotas.Last.Amount));
        quotas.NextStatus = "quota_exceeded";
        Assert.Equal(429, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(Context(Json(4)), trust, true, CancellationToken.None)));
        quotas.NextStatus = "idempotency_conflict";
        Assert.Equal(409, Status(await CloudVoiceQuotaEndpoints.ReserveAsync(Context(Json(4)), trust, true, CancellationToken.None)));
        Assert.Equal(200, Status(await CloudVoiceQuotaEndpoints.LookupAsync(Context(""), trust, true, id, CancellationToken.None)));
        Assert.Equal((tenant, application, "voice.synthesize", id), quotas.Lookup);
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private sealed class QuotaStore : ICloudQuotaStore
    {
        public int Calls { get; private set; }
        public string NextStatus { get; set; } = "reserved";
        public CloudQuotaRequest? Last { get; private set; }
        public (Guid, Guid, string, Guid) Lookup { get; private set; }
        public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
        { Calls++; Last = request; return Task.FromResult(new CloudQuotaResult(NextStatus, NextStatus == "reserved" ? Guid.NewGuid() : null)); }
        public Task<CloudFingerprintKeyLookup> FindFingerprintKeyAsync(Guid tenantId, Guid applicationId, string capabilityId, Guid requestId, CancellationToken cancellationToken)
        { Lookup = (tenantId, applicationId, capabilityId, requestId); return Task.FromResult(new CloudFingerprintKeyLookup("not_found")); }
        public Task<CloudQuotaResult> FinalizeAsync(Guid tenantId, Guid applicationId, Guid reservationId, long actual, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapabilityReader(CloudCapabilitySnapshot snapshot) : ICloudMachineCapabilityReader
    {
        public Task<CloudCapabilitySnapshot?> GetCurrentAsync(CloudCredentialIdentity identity, CancellationToken cancellationToken) => Task.FromResult<CloudCapabilitySnapshot?>(snapshot);
    }

    private sealed class CredentialStore(CloudCredential credential) : ICloudCredentialStore, ICloudCredentialSession
    {
        public Task<ICloudCredentialSession> OpenAsync(bool transactional, CancellationToken cancellationToken) => Task.FromResult<ICloudCredentialSession>(this);
        public Task<CloudCredential?> FindAsync(Guid credentialId, Guid? tenantId, Guid? applicationId, CancellationToken cancellationToken) => Task.FromResult<CloudCredential?>(credentialId == credential.Id ? credential : null);
        public Task<bool> TouchIfActiveAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<string?> FindRoleAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudCredentialApplication?> FindApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudCredential>> ListAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudCapabilitySnapshot?> GetCapabilitiesAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Add(CloudCredential value) => throw new NotSupportedException();
        public void AddAudit(CloudSubject subject, Guid tenantId, string role, string action, Guid credentialId, DateTimeOffset now) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
