using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudAnalyticsQuotaEndpointTests
{
    [Fact]
    public async Task RequiresPinnedTlsAndBindsReservationToAuthorizedIdentity()
    {
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=analytics-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
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
            Scopes = "[\"analytics.analyze\"]",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var snapshot = new CloudCapabilitySnapshot(
            [new CloudCapabilityDefinition { Id = "analytics.analyze", Name = "Analytics", Description = "fixture", ContractVersion = "openconnect.v1",
                BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" }],
            new Dictionary<string, CloudTenantEntitlement> { ["analytics.analyze"] = new() { TenantId = tenant, CapabilityId = "analytics.analyze", PlanGranted = true } },
            new Dictionary<string, CloudTenantCapabilitySelection> { ["analytics.analyze"] = new() { TenantId = tenant, CapabilityId = "analytics.analyze", Enabled = true } },
            new Dictionary<string, CloudApplicationCapabilitySelection> { ["analytics.analyze"] = new() { TenantId = tenant, ApplicationId = application, CapabilityId = "analytics.analyze", Enabled = true } });
        var quotas = new QuotaStore();
        var evidence = new EvidenceStore();
        var claims = new ClaimStore();
        var deployments = new DeploymentReader();
        var completions = new CompletionStore();
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System))
            .AddSingleton<ICloudMachineCapabilityReader>(new CapabilityReader(snapshot))
            .AddSingleton<CloudMachineAuthorizationService>().AddSingleton<ICloudQuotaStore>(quotas)
            .AddSingleton<CloudQuotaService>()
            .AddSingleton(new CloudAnalyticsPrivacyService(new RulesSecurityDetector()))
            .AddSingleton<ICloudAnalyticsPrivacyEvidenceStore>(evidence)
            .AddSingleton<ICloudExecutionClaimStore>(claims).AddSingleton<CloudExecutionClaimService>()
            .AddSingleton<ICloudAnalyticsDeploymentReader>(deployments)
            .AddSingleton<ICloudAnalyticsCompletionStore>(completions).BuildServiceProvider();
        var id = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            requestId = id,
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
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
        var plain = Context(json);
        plain.Request.Scheme = "http";
        Assert.Equal(401, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(plain, trust, true, CancellationToken.None)));
        var noCert = Context(json);
        noCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(noCert, trust, true, CancellationToken.None)));
        Assert.Equal(503, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(json), trust, false, CancellationToken.None)));
        var duplicate = Context(json.Replace("{", "{\"requestId\":\"" + id + "\",", StringComparison.Ordinal));
        Assert.Equal(400, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(duplicate, trust, true, CancellationToken.None)));
        Assert.Equal(400, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(json.Replace("{", "{\"tenantId\":\"other\",", StringComparison.Ordinal)), trust, true, CancellationToken.None)));
        var expired = System.Text.Json.JsonSerializer.Serialize(new
        {
            requestId = id,
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        Assert.Equal(400, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(expired), trust, true, CancellationToken.None)));
        var oversized = Context(new string(' ', 1025));
        Assert.Equal(400, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(oversized, trust, true, CancellationToken.None)));
        var compressed = Context(json);
        compressed.Request.Headers.ContentEncoding = "gzip";
        Assert.Equal(400, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(compressed, trust, true, CancellationToken.None)));
        credential.Scopes = "[]";
        Assert.Equal(403, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(json), trust, true, CancellationToken.None)));
        credential.Scopes = "[\"analytics.analyze\"]";
        snapshot.Entitlements["analytics.analyze"].PlanGranted = false;
        Assert.Equal(403, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(json), trust, true, CancellationToken.None)));
        snapshot.Entitlements["analytics.analyze"].PlanGranted = true;
        Assert.Equal(0, quotas.Reservations);
        var accepted = Context(json);
        Assert.Equal(200, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(accepted, trust, true, CancellationToken.None)));
        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.Equal(tenant, quotas.Last!.TenantId);
        Assert.Equal(application, quotas.Last.ApplicationId);
        Assert.Equal("analytics.analyze", quotas.Last.CapabilityId);
        Assert.Equal("request", quotas.Last.Unit);
        Assert.Equal(id, quotas.Last.RequestId);
        quotas.NextStatus = "quota_exceeded";
        Assert.Equal(429, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(json), trust, true, CancellationToken.None)));
        quotas.NextStatus = "idempotency_conflict";
        Assert.Equal(409, Status(await CloudAnalyticsQuotaEndpoints.ReserveAsync(Context(json), trust, true, CancellationToken.None)));
        Assert.Equal(200, Status(await CloudAnalyticsQuotaEndpoints.LookupAsync(Context(""), trust, true, id, CancellationToken.None)));
        Assert.Equal((tenant, application, "analytics.analyze", id), quotas.Lookup);

        var privacyJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.cloud.analytics/v1",
            requestId = id,
            capability = "analytics.analyze",
            input = new { feature = "weight", values = new[] { 1, 2 } }
        });
        DefaultHttpContext PrivacyContext(string value)
        {
            var context = Context(value);
            context.Request.Headers["X-Cloud-Request-Id"] = id.ToString();
            context.Request.Headers["X-Cloud-Fingerprint-Key-Id"] = "active";
            context.Request.Headers["X-Cloud-Request-Fingerprint"] = Convert.ToBase64String(new byte[32]);
            return context;
        }
        var privacyNoCert = PrivacyContext(privacyJson);
        privacyNoCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudAnalyticsPrivacyEndpoints.EvaluateAsync(privacyNoCert, trust, true, CancellationToken.None)));
        Assert.Equal(400, Status(await CloudAnalyticsPrivacyEndpoints.EvaluateAsync(PrivacyContext(privacyJson.Replace("{", "{\"requestId\":\"" + id + "\",", StringComparison.Ordinal)), trust, true, CancellationToken.None)));
        var mismatched = PrivacyContext(privacyJson);
        mismatched.Request.Headers["X-Cloud-Request-Id"] = Guid.NewGuid().ToString();
        Assert.Equal(400, Status(await CloudAnalyticsPrivacyEndpoints.EvaluateAsync(mismatched, trust, true, CancellationToken.None)));
        Assert.Equal(0, evidence.Calls);
        Assert.Equal(200, Status(await CloudAnalyticsPrivacyEndpoints.EvaluateAsync(PrivacyContext(privacyJson), trust, true, CancellationToken.None)));
        Assert.Equal((tenant, application, id, "allowed"), evidence.Last);
        Assert.Equal("request", evidence.Direction);
        var sensitive = privacyJson.Replace("weight", "person@example.test", StringComparison.Ordinal);
        Assert.Equal(403, Status(await CloudAnalyticsPrivacyEndpoints.EvaluateAsync(PrivacyContext(sensitive), trust, true, CancellationToken.None)));
        Assert.Equal((tenant, application, id, "privacy_blocked"), evidence.Last);
        Assert.Contains(evidence.Categories, category => category.Category == "EMAIL" && category.Count >= 1);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            taskId = id,
            kind = "analytics.analyze",
            result = new { lineage = "person@example.test" }
        });
        Assert.Equal(403, Status(await CloudAnalyticsPrivacyEndpoints.EvaluateAsync(PrivacyContext(responseJson), trust, true, CancellationToken.None, response: true)));
        Assert.Equal("response", evidence.Direction);
        Assert.Equal((tenant, application, id, "privacy_blocked"), evidence.Last);
        var claimJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            fingerprintKeyId = "active",
            fingerprint = Convert.ToBase64String(new byte[32]),
            workerPayloadFingerprint = Convert.ToBase64String(new byte[32]),
            endpointReference = "analytics",
            region = "region-a",
            contractVersion = "openconnect.v1",
            modelRevision = "rev-1",
            modelDigest = "sha256:" + new string('a', 64)
        });
        var invalidClaim = Context(claimJson.Replace("workerPayloadFingerprint", "unknownField", StringComparison.Ordinal));
        Assert.Equal(400, Status(await CloudAnalyticsClaimEndpoints.ClaimAsync(id, invalidClaim, trust, true, CancellationToken.None)));
        Assert.Equal(0, claims.Calls);
        Assert.Equal(200, Status(await CloudAnalyticsClaimEndpoints.ClaimAsync(id, Context(claimJson), trust, true, CancellationToken.None)));
        Assert.Equal((tenant, application, id, "analytics.analyze"), claims.Last);
        var deploymentJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            endpointReference = "analytics",
            region = "region-a",
            contractVersion = "openconnect.v1",
            modelRevision = "rev-1",
            modelDigest = "sha256:" + new string('a', 64)
        });
        Assert.Equal(200, Status(await CloudAnalyticsDeploymentEndpoints.MatchAsync(Context(deploymentJson), trust, true, CancellationToken.None)));
        Assert.Equal(tenant, deployments.Tenant);
        Assert.Equal(400, Status(await CloudAnalyticsCompletionEndpoints.CompleteAsync(id, id, Context("body"), trust, true, CancellationToken.None)));
        Assert.Equal(0, completions.Calls);
        Assert.Equal(400, Status(await CloudAnalyticsCompletionEndpoints.CompleteAsync(id, id, Context("{\"computeMilliseconds\":0}"), trust, true, CancellationToken.None)));
        Assert.Equal(200, Status(await CloudAnalyticsCompletionEndpoints.CompleteAsync(id, id, Context("{\"computeMilliseconds\":14}"), trust, true, CancellationToken.None)));
        Assert.Equal((tenant, application, id, id), completions.Last);
        Assert.Equal(14, completions.Milliseconds);
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private sealed class EvidenceStore : ICloudAnalyticsPrivacyEvidenceStore
    {
        public int Calls { get; private set; }
        public string? Direction { get; private set; }
        public IReadOnlyList<CloudPrivacyCategoryCount> Categories { get; private set; } = [];
        public (Guid, Guid, Guid, string) Last { get; private set; }
        public Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
            string direction, string outcome, string traceId, IReadOnlyList<CloudPrivacyCategoryCount> categories, CancellationToken cancellationToken)
        {
            Calls++;
            Direction = direction;
            Categories = categories;
            Last = (identity.TenantId, identity.ApplicationId, requestId, outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class ClaimStore : ICloudExecutionClaimStore
    {
        public int Calls { get; private set; }
        public (Guid, Guid, Guid, string) Last { get; private set; }
        public Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId, Guid reservationId,
            string capabilityId, string fingerprintKeyId, byte[] fingerprint, CloudClassificationDeploymentMatchRequest? deployment,
            CancellationToken cancellationToken, byte[]? workerPayloadFingerprint = null, string? inferenceModelId = null,
            CloudInferenceDeploymentMatch? inferenceDeployment = null)
        {
            Calls++;
            Last = (tenantId, applicationId, reservationId, capabilityId);
            return Task.FromResult(new CloudExecutionClaimResult("claimed", Guid.NewGuid()));
        }
    }

    private sealed class DeploymentReader : ICloudAnalyticsDeploymentReader
    {
        public Guid Tenant { get; private set; }
        public Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken)
        { Tenant = tenantId; return Task.FromResult(true); }
    }

    private sealed class CompletionStore : ICloudAnalyticsCompletionStore
    {
        public int Calls { get; private set; }
        public (Guid, Guid, Guid, Guid) Last { get; private set; }
        public int Milliseconds { get; private set; }
        public Task<CloudExecutionCompletionResult> CompleteAsync(Guid tenantId, Guid applicationId, Guid reservationId, Guid claimId, int computeMilliseconds, CancellationToken cancellationToken)
        { Calls++; Milliseconds = computeMilliseconds; Last = (tenantId, applicationId, reservationId, claimId); return Task.FromResult(new CloudExecutionCompletionResult("completed")); }
    }

    private sealed class QuotaStore : ICloudQuotaStore
    {
        public int Reservations { get; private set; }
        public string NextStatus { get; set; } = "reserved";
        public CloudQuotaRequest? Last { get; private set; }
        public (Guid, Guid, string, Guid) Lookup { get; private set; }
        public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
        { Reservations++; Last = request; return Task.FromResult(new CloudQuotaResult(NextStatus, NextStatus == "reserved" ? Guid.NewGuid() : null)); }
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
