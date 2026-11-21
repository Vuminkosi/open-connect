using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudKnowledgeEmbeddingPrivacyEndpointTests
{
    [Fact]
    public async Task RequiresTrustedAuthorizedCallerAndRecordsBothDirections()
    {
        using var key = RSA.Create(2048);
        var certRequest = new CertificateRequest("CN=privacy-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var trust = new CloudGatewayClientTrust([Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))]);
        var material = CloudCredentialSecrets.Create("Development");
        var tenant = Guid.NewGuid(); var application = Guid.NewGuid(); var id = Guid.NewGuid();
        var credential = new CloudCredential { Id = material.Id, TenantId = tenant, ApplicationId = application,
            Environment = "Development", KeyHash = material.Hash, Fingerprint = material.Fingerprint,
            Scopes = "[\"knowledge.embed\"]", IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        var snapshot = new CloudCapabilitySnapshot(
            [new CloudCapabilityDefinition { Id = "knowledge.embed", Name = "Embedding", Description = "fixture", ContractVersion = "openconnect.v1",
                BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" }],
            new Dictionary<string, CloudTenantEntitlement> { ["knowledge.embed"] = new() { TenantId = tenant, CapabilityId = "knowledge.embed", PlanGranted = true } },
            new Dictionary<string, CloudTenantCapabilitySelection> { ["knowledge.embed"] = new() { TenantId = tenant, CapabilityId = "knowledge.embed", Enabled = true } },
            new Dictionary<string, CloudApplicationCapabilitySelection> { ["knowledge.embed"] = new() { TenantId = tenant, ApplicationId = application, CapabilityId = "knowledge.embed", Enabled = true } });
        var evidence = new EvidenceStore();
        var quota = new QuotaStore();
        var claims = new ClaimStore();
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System))
            .AddSingleton<ICloudMachineCapabilityReader>(new CapabilityReader(snapshot))
            .AddSingleton<CloudMachineAuthorizationService>()
            .AddSingleton<ICloudQuotaStore>(quota).AddSingleton<CloudQuotaService>()
            .AddSingleton<ICloudExecutionClaimStore>(claims).AddSingleton<CloudExecutionClaimService>()
            .AddSingleton<ICloudKnowledgeEmbeddingDeploymentReader>(new DeploymentReader())
            .AddSingleton(new CloudJsonPrivacyService(new RulesSecurityDetector()))
            .AddSingleton<ICloudKnowledgeEmbeddingPrivacyEvidenceStore>(evidence).BuildServiceProvider();
        DefaultHttpContext Context(string text, bool response = false)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(response
                ? (object)new { schemaVersion = "openconnect.v1", taskId = id, kind = "knowledge.embed", result = new { text } }
                : new { schemaVersion = "openconnect.cloud.knowledge-embed/v1", requestId = id, capability = "knowledge.embed", input = new { inputKind = "query", texts = new[] { text } } });
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https"; context.Connection.ClientCertificate = certificate;
            context.Request.Headers.Authorization = "Bearer " + material.Secret;
            context.Request.Headers["X-Cloud-Request-Id"] = id.ToString();
            context.Request.Headers["X-Cloud-Fingerprint-Key-Id"] = "active";
            context.Request.Headers["X-Cloud-Request-Fingerprint"] = Convert.ToBase64String(new byte[32]);
            context.Request.ContentType = "application/json"; context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return context;
        }
        var untrusted = Context("safe"); untrusted.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudKnowledgeEmbeddingPrivacyEndpoints.EvaluateAsync(untrusted, trust, true, CancellationToken.None)));
        credential.Scopes = "[]";
        Assert.Equal(403, Status(await CloudKnowledgeEmbeddingPrivacyEndpoints.EvaluateAsync(Context("safe"), trust, true, CancellationToken.None)));
        credential.Scopes = "[\"knowledge.embed\"]";
        Assert.Equal(200, Status(await CloudKnowledgeEmbeddingPrivacyEndpoints.EvaluateAsync(Context("forest"), trust, true, CancellationToken.None)));
        Assert.Equal(("request", "allowed"), evidence.Last);
        Assert.Equal(6, evidence.InputCharacters);
        Assert.Equal(200, Status(await CloudKnowledgeEmbeddingPrivacyEndpoints.EvaluateAsync(Context("a😀"), trust, true, CancellationToken.None)));
        Assert.Equal(2, evidence.InputCharacters);
        Assert.Equal(403, Status(await CloudKnowledgeEmbeddingPrivacyEndpoints.EvaluateAsync(Context("person@example.test"), trust, true, CancellationToken.None)));
        Assert.Equal(("request", "privacy_blocked"), evidence.Last);
        Assert.Contains(evidence.Categories, item => item.Category == "EMAIL" && item.Count > 0);
        Assert.Equal(403, Status(await CloudKnowledgeEmbeddingPrivacyEndpoints.EvaluateAsync(Context("person@example.test", true), trust, true, CancellationToken.None, response: true)));
        Assert.Equal(("response", "privacy_blocked"), evidence.Last);
        Assert.Null(evidence.InputCharacters);
        var quotaContext = Context("unused");
        quotaContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        { requestId = id, fingerprintKeyId = "active", fingerprint = Convert.ToBase64String(new byte[32]), expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) })));
        Assert.Equal(200, Status(await CloudKnowledgeEmbeddingQuotaEndpoints.ReserveAsync(quotaContext, trust, true, CancellationToken.None)));
        Assert.Equal(tenant, quota.Last!.TenantId);
        Assert.Equal(application, quota.Last.ApplicationId);
        Assert.Equal("knowledge.embed", quota.Last.CapabilityId);
        Assert.Equal("request", quota.Last.Unit);
        Assert.Equal(id, quota.Last.RequestId);
        var claimContext = Context("unused");
        claimContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        { fingerprintKeyId = "active", fingerprint = Convert.ToBase64String(new byte[32]),
            workerPayloadFingerprint = Convert.ToBase64String(new byte[32]), endpointReference = "embedding", region = "region-a",
            contractVersion = "openconnect.v1", modelRevision = "revision", modelDigest = "sha256:" + new string('a', 64) })));
        Assert.Equal(200, Status(await CloudKnowledgeEmbeddingClaimEndpoints.ClaimAsync(id, claimContext, trust, true, CancellationToken.None)));
        Assert.Equal((tenant, application, id, "knowledge.embed"), claims.Last);
        var deploymentContext = Context("unused");
        deploymentContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        { endpointReference = "embedding", region = "region-a", contractVersion = "openconnect.v1", modelRevision = "revision", modelDigest = "sha256:" + new string('a', 64) })));
        Assert.Equal(200, Status(await CloudKnowledgeEmbeddingDeploymentEndpoints.MatchAsync(deploymentContext, trust, true, CancellationToken.None)));
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private sealed class DeploymentReader : ICloudKnowledgeEmbeddingDeploymentReader
    {
        public Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class ClaimStore : ICloudExecutionClaimStore
    {
        public (Guid, Guid, Guid, string) Last { get; private set; }
        public Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId, Guid reservationId,
            string capabilityId, string fingerprintKeyId, byte[] fingerprint, CloudClassificationDeploymentMatchRequest? deployment,
            CancellationToken cancellationToken, byte[]? workerPayloadFingerprint = null, string? inferenceModelId = null,
            CloudInferenceDeploymentMatch? inferenceDeployment = null)
        { Last = (tenantId, applicationId, reservationId, capabilityId); return Task.FromResult(new CloudExecutionClaimResult("claimed", Guid.NewGuid())); }
    }
    private sealed class QuotaStore : ICloudQuotaStore
    {
        public CloudQuotaRequest? Last { get; private set; }
        public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
        { Last = request; return Task.FromResult(new CloudQuotaResult("reserved", Guid.NewGuid())); }
        public Task<CloudFingerprintKeyLookup> FindFingerprintKeyAsync(Guid tenantId, Guid applicationId, string capabilityId, Guid requestId, CancellationToken cancellationToken) => Task.FromResult(new CloudFingerprintKeyLookup("not_found"));
        public Task<CloudQuotaResult> FinalizeAsync(Guid tenantId, Guid applicationId, Guid reservationId, long actual, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class EvidenceStore : ICloudKnowledgeEmbeddingPrivacyEvidenceStore
    {
        public (string, string) Last { get; private set; }
        public IReadOnlyList<CloudPrivacyCategoryCount> Categories { get; private set; } = [];
        public int? InputCharacters { get; private set; }
        public Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
            string direction, string outcome, string traceId, IReadOnlyList<CloudPrivacyCategoryCount> categories, int? inputCharacters, CancellationToken cancellationToken)
        { Last = (direction, outcome); Categories = categories; InputCharacters = inputCharacters; return Task.CompletedTask; }
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
