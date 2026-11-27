using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudTranslationPrivacyEndpointTests
{
    [Fact]
    public async Task RequiresTrustedGatewayScopedCredentialAndDurableEvidence()
    {
        using var key = RSA.Create(2048);
        var certRequest = new CertificateRequest("CN=translation-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
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
            Scopes = "[\"translation.translate\"]",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var snapshot = new CloudCapabilitySnapshot(
            [new CloudCapabilityDefinition { Id = "translation.translate", Name = "Translation", Description = "fixture", ContractVersion = "v1",
                BillingUnit = "character", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" }],
            new Dictionary<string, CloudTenantEntitlement> { ["translation.translate"] = new() { TenantId = tenant, CapabilityId = "translation.translate", PlanGranted = true } },
            new Dictionary<string, CloudTenantCapabilitySelection> { ["translation.translate"] = new() { TenantId = tenant, CapabilityId = "translation.translate", Enabled = true } },
            new Dictionary<string, CloudApplicationCapabilitySelection> { ["translation.translate"] = new() { TenantId = tenant, ApplicationId = application, CapabilityId = "translation.translate", Enabled = true } });
        var evidence = new EvidenceStore();
        var quota = new TranslationQuotaStore();
        var deploymentReader = new TranslationDeploymentReader();
        var claimStore = new TranslationClaimStore();
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System))
            .AddSingleton<ICloudMachineCapabilityReader>(new CapabilityReader(snapshot)).AddSingleton<CloudMachineAuthorizationService>()
            .AddSingleton(new CloudTranslationPrivacyService(new RulesSecurityDetector()))
            .AddSingleton<ICloudTranslationQuotaStore>(quota).AddSingleton<CloudTranslationQuota>()
            .AddSingleton<ICloudTranslationDeploymentReader>(deploymentReader).AddSingleton<CloudTranslationDeploymentMatch>()
            .AddSingleton<ICloudTranslationClaimStore>(claimStore).AddSingleton<CloudTranslationClaim>()
            .AddSingleton<ICloudTranslationPrivacyEvidenceStore>(evidence).BuildServiceProvider();
        var id = Guid.NewGuid();
        DefaultHttpContext Context(string body)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https";
            context.Connection.ClientCertificate = certificate;
            context.Request.Headers.Authorization = "Bearer " + material.Secret;
            context.Request.Headers["X-Cloud-Request-Id"] = id.ToString("D");
            context.Request.Headers["X-Cloud-Fingerprint-Key-Id"] = "active";
            context.Request.Headers["X-Cloud-Request-Fingerprint"] = Convert.ToBase64String(new byte[32]);
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return context;
        }
        const string safe = "{\"text\":\"hello\",\"sourceLang\":\"eng_Latn\",\"targetLang\":\"fra_Latn\"}";
        var noCert = Context(safe);
        noCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(noCert, trust, true, default)));
        Assert.Equal(503, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(Context(safe), trust, false, default)));
        var noToken = Context(safe);
        noToken.Request.Headers.Authorization = "Bearer invalid";
        Assert.Equal(401, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(noToken, trust, true, default)));
        credential.Scopes = "[\"voice.synthesize\"]";
        Assert.Equal(403, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(Context(safe), trust, true, default)));
        credential.Scopes = "[\"translation.translate\"]";
        Assert.Equal(400, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(Context(safe.Replace("{", "{\"text\":\"duplicate\",", StringComparison.Ordinal)), trust, true, default)));
        var encoded = Context(safe);
        encoded.Request.Headers.ContentEncoding = "gzip";
        Assert.Equal(415, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(encoded, trust, true, default)));
        Assert.Equal(0, evidence.Calls);
        var accepted = Context(safe);
        Assert.Equal(200, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(accepted, trust, true, default)));
        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.Equal((tenant, application, id, 5, "allowed"), evidence.Last);
        Assert.Equal(403, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(Context(safe.Replace("hello", "person@example.test", StringComparison.Ordinal)), trust, true, default)));
        Assert.Equal("privacy_blocked", evidence.Last.Item5);
        evidence.Fail = true;
        Assert.Equal(503, Status(await CloudTranslationPrivacyEndpoints.EvaluateAsync(Context(safe), trust, true, default)));
        string Reserve(long characters) => System.Text.Json.JsonSerializer.Serialize(new
        {
            requestId = id,
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
            characters,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        var quotaNoCert = Context(Reserve(5));
        quotaNoCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudTranslationQuotaEndpoints.ReserveAsync(quotaNoCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudTranslationQuotaEndpoints.ReserveAsync(Context(Reserve(5001)), trust, true, default)));
        Assert.Equal(400, Status(await CloudTranslationQuotaEndpoints.ReserveAsync(Context(Reserve(5).Replace("{", "{\"characters\":5,", StringComparison.Ordinal)), trust, true, default)));
        Assert.Equal(0, quota.Calls);
        Assert.Equal(200, Status(await CloudTranslationQuotaEndpoints.ReserveAsync(Context(Reserve(5)), trust, true, default)));
        Assert.Equal((tenant, application, id, "translation.translate", "character", 5L),
            (quota.Last!.TenantId, quota.Last.ApplicationId, quota.Last.RequestId, quota.Last.CapabilityId, quota.Last.Unit, quota.Last.Amount));
        quota.Status = "privacy_unavailable";
        Assert.Equal(503, Status(await CloudTranslationQuotaEndpoints.ReserveAsync(Context(Reserve(5)), trust, true, default)));
        quota.Status = "quota_exceeded";
        Assert.Equal(429, Status(await CloudTranslationQuotaEndpoints.ReserveAsync(Context(Reserve(5)), trust, true, default)));
        var matchBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            deploymentId = id,
            endpointReference = "nllb",
            region = "local",
            contractVersion = "v1",
            modelId = "nllb",
            modelRevision = "r1",
            modelDigest = "sha256:" + new string('a', 64),
            imageDigest = "sha256:" + new string('b', 64),
            tokenizerHash = new string('c', 64),
            sourceLang = "eng_Latn",
            targetLang = "fra_Latn"
        });
        var matchNoCert = Context(matchBody);
        matchNoCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudTranslationDeploymentEndpoints.MatchAsync(matchNoCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudTranslationDeploymentEndpoints.MatchAsync(Context(matchBody.Replace("{", "{\"deploymentId\":\"" + id + "\",", StringComparison.Ordinal)), trust, true, default)));
        var matched = await CloudTranslationDeploymentEndpoints.MatchAsync(Context(matchBody), trust, true, default);
        Assert.Equal(200, Status(matched) ?? 200);
        Assert.NotNull((matched as IValueHttpResult)?.Value);
        Assert.Equal(tenant, deploymentReader.TenantId);
        Assert.Equal("fra_Latn", deploymentReader.Last!.TargetLang);
        var proof = System.Text.Json.JsonSerializer.Serialize(new
        {
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
            workerPayloadFingerprint = Convert.ToBase64String(new byte[32]),
            deployment = System.Text.Json.JsonSerializer.Deserialize<object>(matchBody)
        });
        var noClaimCert = Context(proof);
        noClaimCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudTranslationClaimEndpoints.ClaimAsync(id, noClaimCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudTranslationClaimEndpoints.ClaimAsync(id, Context(proof.Replace("\"deployment\":{", "\"deployment\":{\"deploymentId\":\"" + id + "\",", StringComparison.Ordinal)), trust, true, default)));
        Assert.Equal(200, Status(await CloudTranslationClaimEndpoints.ClaimAsync(id, Context(proof), trust, true, default)));
        Assert.Equal((tenant, application, id, "fra_Latn"), claimStore.Last);
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private sealed class TranslationDeploymentReader : ICloudTranslationDeploymentReader
    {
        public Guid TenantId { get; private set; }
        public CloudTranslationDeploymentMatchRequest? Last { get; private set; }
        public Task<bool> MatchesAsync(Guid tenantId, CloudTranslationDeploymentMatchRequest request, CancellationToken cancellationToken)
        { TenantId = tenantId; Last = request; return Task.FromResult(true); }
    }

    private sealed class TranslationClaimStore : ICloudTranslationClaimStore
    {
        public (Guid, Guid, Guid, string) Last { get; private set; }
        public Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
            byte[] fingerprint, byte[] workerFingerprint, CloudTranslationDeploymentMatchRequest deployment, CancellationToken cancellationToken)
        {
            Last = (identity.TenantId, identity.ApplicationId, reservationId, deployment.TargetLang);
            return Task.FromResult(new CloudExecutionClaimResult("claimed", Guid.NewGuid()));
        }
    }

    private sealed class TranslationQuotaStore : ICloudTranslationQuotaStore
    {
        public int Calls { get; private set; }
        public string Status { get; set; } = "reserved";
        public CloudQuotaRequest? Last { get; private set; }
        public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            return Task.FromResult(new CloudQuotaResult(Status, Status == "reserved" ? Guid.NewGuid() : null));
        }
    }

    private sealed class EvidenceStore : ICloudTranslationPrivacyEvidenceStore
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
