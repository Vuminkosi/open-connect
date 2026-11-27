using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
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
        var tenant = Guid.NewGuid();
        var application = Guid.NewGuid();
        var credential = new CloudCredential
        {
            Id = material.Id,
            TenantId = tenant,
            ApplicationId = application,
            Environment = "Development",
            KeyHash = material.Hash,
            Fingerprint = material.Fingerprint,
            Scopes = "[\"voice.synthesize\"]",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var snapshot = new CloudCapabilitySnapshot(
            [new CloudCapabilityDefinition { Id = "voice.synthesize", Name = "Voice", Description = "fixture", ContractVersion = "v1",
                BillingUnit = "character", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" }],
            new Dictionary<string, CloudTenantEntitlement> { ["voice.synthesize"] = new() { TenantId = tenant, CapabilityId = "voice.synthesize", PlanGranted = true } },
            new Dictionary<string, CloudTenantCapabilitySelection> { ["voice.synthesize"] = new() { TenantId = tenant, CapabilityId = "voice.synthesize", Enabled = true } },
            new Dictionary<string, CloudApplicationCapabilitySelection> { ["voice.synthesize"] = new() { TenantId = tenant, ApplicationId = application, CapabilityId = "voice.synthesize", Enabled = true } });
        var quotas = new QuotaStore();
        var evidence = new EvidenceStore();
        var claims = new ClaimStore();
        var access = new AccessStore();
        var outputEvidence = new OutputEvidenceStore();
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System))
            .AddSingleton<ICloudMachineCapabilityReader>(new CapabilityReader(snapshot))
            .AddSingleton<CloudMachineAuthorizationService>().AddSingleton<ICloudQuotaStore>(quotas)
            .AddSingleton<CloudQuotaService>().AddSingleton(new CloudVoicePrivacyService(new RulesSecurityDetector()))
            .AddSingleton<ICloudVoicePrivacyEvidenceStore>(evidence).AddSingleton<ICloudVoiceClaimStore>(claims)
            .AddSingleton<CloudVoiceClaimService>().AddSingleton<ICloudVoiceAccessStore>(access)
            .AddSingleton(new CloudVoiceOutputPrivacyService(new RulesSecurityDetector()))
            .AddSingleton<ICloudVoiceOutputEvidenceStore>(outputEvidence).BuildServiceProvider();
        var id = Guid.NewGuid();
        string Json(int characters) => System.Text.Json.JsonSerializer.Serialize(new
        {
            requestId = id,
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
            characters,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        DefaultHttpContext Context(string body)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https";
            context.Connection.ClientCertificate = certificate;
            context.Request.Headers.Authorization = "Bearer " + material.Secret;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return context;
        }
        var noCert = Context(Json(4));
        noCert.Connection.ClientCertificate = null;
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

        DefaultHttpContext PrivacyContext(string body)
        {
            var context = Context(body);
            context.Request.Headers["X-Cloud-Request-Id"] = id.ToString("D");
            context.Request.Headers["X-Cloud-Fingerprint-Key-Id"] = "active";
            context.Request.Headers["X-Cloud-Request-Fingerprint"] = Convert.ToBase64String(new byte[32]);
            return context;
        }
        var safe = "{\"text\":\"hello 🌍\",\"voiceId\":\"af_heart\",\"locale\":\"en-US\",\"format\":\"wav\"}";
        var missingCert = PrivacyContext(safe);
        missingCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudVoicePrivacyEndpoints.EvaluateAsync(missingCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudVoicePrivacyEndpoints.EvaluateAsync(PrivacyContext(safe.Replace("{", "{\"text\":\"duplicate\",", StringComparison.Ordinal)), trust, true, default)));
        Assert.Equal(0, evidence.Calls);
        Assert.Equal(200, Status(await CloudVoicePrivacyEndpoints.EvaluateAsync(PrivacyContext(safe), trust, true, default)));
        Assert.Equal((tenant, application, id, 7, "allowed"), evidence.Last);
        Assert.Equal(403, Status(await CloudVoicePrivacyEndpoints.EvaluateAsync(PrivacyContext(safe.Replace("hello 🌍", "person@example.test", StringComparison.Ordinal)), trust, true, default)));
        Assert.Equal("privacy_blocked", evidence.Last.Item5);
        evidence.Fail = true;
        Assert.Equal(503, Status(await CloudVoicePrivacyEndpoints.EvaluateAsync(PrivacyContext(safe), trust, true, default)));

        var claimJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
            workerPayloadFingerprint = Convert.ToBase64String(new byte[32]),
            endpointReference = "voice-worker",
            region = "region-a",
            contractVersion = "v1",
            modelId = "kokoro",
            modelRevision = "revision-1",
            modelDigest = "sha256:" + new string('a', 64)
        });
        var claimNoCert = Context(claimJson);
        claimNoCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudVoiceClaimEndpoints.ClaimAsync(id, claimNoCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudVoiceClaimEndpoints.ClaimAsync(id, Context(claimJson.Replace("workerPayloadFingerprint", "unknown", StringComparison.Ordinal)), trust, true, default)));
        Assert.Equal(0, claims.Calls);
        Assert.Equal(200, Status(await CloudVoiceClaimEndpoints.ClaimAsync(id, Context(claimJson), trust, true, default)));
        Assert.Equal((tenant, application, id), claims.Last);
        Assert.Equal(401, Status(await CloudVoiceAccessEndpoints.CheckAsync(id, id, claimNoCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudVoiceAccessEndpoints.CheckAsync(Guid.Empty, id, Context(""), trust, true, default)));
        Assert.Equal(200, Status(await CloudVoiceAccessEndpoints.CheckAsync(id, id, Context(""), trust, true, default)));
        Assert.Equal((tenant, application, id, id), access.Last);

        DefaultHttpContext OutputContext(string transcript)
        {
            var context = Context(System.Text.Json.JsonSerializer.Serialize(new { transcript, modelFingerprint = new string('a', 64) }));
            context.Request.Headers["X-Cloud-Request-Id"] = id.ToString("D");
            context.Request.Headers["X-Cloud-Reservation-Id"] = id.ToString("D");
            context.Request.Headers["X-Cloud-Claim-Id"] = id.ToString("D");
            return context;
        }
        var outputNoCert = OutputContext("hello");
        outputNoCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudVoiceOutputPrivacyEndpoints.InspectAsync(outputNoCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudVoiceOutputPrivacyEndpoints.InspectAsync(OutputContext(" "), trust, true, default)));
        Assert.Equal(200, Status(await CloudVoiceOutputPrivacyEndpoints.InspectAsync(OutputContext("hello"), trust, true, default)));
        Assert.Equal((tenant, application, id, "allowed"), outputEvidence.Last);
        Assert.Equal(403, Status(await CloudVoiceOutputPrivacyEndpoints.InspectAsync(OutputContext("person@example.test"), trust, true, default)));
        Assert.Equal("privacy_blocked", outputEvidence.Last.Item4);
        outputEvidence.Accept = false;
        Assert.Equal(503, Status(await CloudVoiceOutputPrivacyEndpoints.InspectAsync(OutputContext("hello"), trust, true, default)));
        access.Allowed = false;
        Assert.Equal(503, Status(await CloudVoiceAccessEndpoints.CheckAsync(id, id, Context(""), trust, true, default)));
        Assert.Equal(503, Status(await CloudVoiceOutputPrivacyEndpoints.InspectAsync(OutputContext("hello"), trust, true, default)));
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private sealed class OutputEvidenceStore : ICloudVoiceOutputEvidenceStore
    {
        public bool Accept { get; set; } = true;
        public (Guid, Guid, Guid, string) Last { get; private set; }
        public Task<bool> AppendForCompletedAsync(CloudCredentialIdentity identity, Guid requestId, Guid reservationId, Guid claimId,
            string modelFingerprint, string outcome, string traceId, CancellationToken cancellationToken)
        { Last = (identity.TenantId, identity.ApplicationId, requestId, outcome); return Task.FromResult(Accept); }
    }

    private sealed class EvidenceStore : ICloudVoicePrivacyEvidenceStore
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public (Guid, Guid, Guid, int, string) Last { get; private set; }
        public Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
            int characters, string outcome, string traceId, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException();
            Calls++;
            Last = (identity.TenantId, identity.ApplicationId, requestId, characters, outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class ClaimStore : ICloudVoiceClaimStore
    {
        public int Calls { get; private set; }
        public (Guid, Guid, Guid) Last { get; private set; }
        public Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
            byte[] fingerprint, byte[] workerFingerprint, string modelId, CloudInferenceDeploymentMatch deployment, CancellationToken cancellationToken)
        { Calls++; Last = (identity.TenantId, identity.ApplicationId, reservationId); return Task.FromResult(new CloudExecutionClaimResult("claimed", Guid.NewGuid())); }
    }

    private sealed class AccessStore : ICloudVoiceAccessStore
    {
        public bool Allowed { get; set; } = true;
        public (Guid, Guid, Guid, Guid) Last { get; private set; }
        public Task<bool> IsClaimAllowedAsync(CloudCredentialIdentity identity, Guid reservationId, Guid claimId, CancellationToken cancellationToken)
        { Last = (identity.TenantId, identity.ApplicationId, reservationId, claimId); return Task.FromResult(Allowed); }
    }

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
