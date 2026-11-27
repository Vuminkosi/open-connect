using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudHostPrivacyEndpointTests
{
    [Fact]
    public async Task Requires_tls_pinned_client_certificate_and_configured_store()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=privacy-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var trust = new CloudGatewayClientTrust([Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))]);
        var context = new DefaultHttpContext();
        context.Connection.ClientCertificate = certificate;
        Assert.Equal(401, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(context, trust, true, CancellationToken.None)));
        context.Request.Scheme = "https";
        context.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(context, trust, true, CancellationToken.None)));
        context.Connection.ClientCertificate = certificate;
        Assert.Equal(503, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(context, trust, false, CancellationToken.None)));
        Assert.Equal(401, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(context, trust, true, CancellationToken.None)));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Host_and_gateway_certificate_pins_are_independent()
    {
        using var hostKey = RSA.Create(2048);
        using var gatewayKey = RSA.Create(2048);
        static X509Certificate2 Certificate(RSA key, string name)
        {
            var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        }
        using var host = Certificate(hostKey, "CN=host");
        using var gateway = Certificate(gatewayKey, "CN=gateway");
        var hostTrust = new CloudGatewayClientTrust([Convert.ToHexString(host.GetCertHash(HashAlgorithmName.SHA256))]);
        var gatewayTrust = new CloudGatewayClientTrust([Convert.ToHexString(gateway.GetCertHash(HashAlgorithmName.SHA256))]);
        Assert.True(hostTrust.IsTrusted(host, DateTimeOffset.UtcNow));
        Assert.False(gatewayTrust.IsTrusted(host, DateTimeOffset.UtcNow));
        Assert.False(hostTrust.IsTrusted(gateway, DateTimeOffset.UtcNow));
        Assert.False(gatewayTrust.Overlaps(hostTrust));
        Assert.True(hostTrust.Overlaps(hostTrust));
        var hostPin = Convert.ToHexString(host.GetCertHash(HashAlgorithmName.SHA256));
        var gatewayPin = Convert.ToHexString(gateway.GetCertHash(HashAlgorithmName.SHA256));
        Assert.True(new CloudWorkerClientTrust([new(Guid.NewGuid(), hostPin)], null).Overlaps(hostTrust));
        Assert.False(new CloudWorkerClientTrust([new(Guid.NewGuid(), gatewayPin)], null).Overlaps(hostTrust));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Connection.ClientCertificate = gateway;
        Assert.Equal(401, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(context, hostTrust, true, CancellationToken.None)));
    }

    [Fact]
    public async Task Valid_credentials_do_not_bypass_payload_bounds_or_tenant_binding()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=privacy-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var trust = new CloudGatewayClientTrust([Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))]);
        var material = CloudCredentialSecrets.Create("Development");
        var credential = new CloudCredential
        {
            Id = material.Id,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ApplicationId = Guid.NewGuid(),
            Environment = "Development",
            KeyHash = material.Hash,
            Fingerprint = material.Fingerprint,
            Scopes = "[]",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var evidenceStore = new EvidenceStore();
        using var services = new ServiceCollection().AddSingleton(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System))
            .AddSingleton<ICloudHostPrivacyEvidenceStore>(evidenceStore).BuildServiceProvider();
        DefaultHttpContext Context(string body, string contentType = "application/json")
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https";
            context.Connection.ClientCertificate = certificate;
            context.Request.Headers.Authorization = "Bearer " + material.Secret;
            context.Request.ContentType = contentType;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return context;
        }
        var wrongType = Context("{}", "text/plain");
        Assert.Equal(415, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(wrongType, trust, true, CancellationToken.None)));
        var encoded = Context("{}");
        encoded.Request.Headers.ContentEncoding = "gzip";
        Assert.Equal(415, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(encoded, trust, true, CancellationToken.None)));
        var oversized = Context(new string('x', 8193));
        Assert.Equal(413, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(oversized, trust, true, CancellationToken.None)));
        var malformed = Context("{");
        Assert.Equal(400, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(malformed, trust, true, CancellationToken.None)));
        var otherTenant = Context("""{"schemaVersion":"openconnect.privacy-evidence.v1","eventId":"11111111-1111-1111-1111-111111111111","tenantId":"00000000000000000000000002","destination":"Output","allowed":false,"action":"Block","categories":{}}""");
        Assert.Equal(400, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(otherTenant, trust, true, CancellationToken.None)));
        Assert.Equal(0, evidenceStore.Calls);
        const string valid = """{"schemaVersion":"openconnect.privacy-evidence.v1","eventId":"11111111-1111-1111-1111-111111111111","tenantId":"00000000000000000000000001","destination":"Output","allowed":false,"action":"Block","categories":{"EMAIL":2}}""";
        var accepted = await CloudHostPrivacyEvidenceEndpoints.HandleAsync(Context(valid), trust, true, CancellationToken.None);
        Assert.Equal(200, Status(accepted));
        Assert.Equal(credential.TenantId, evidenceStore.Identity!.TenantId);
        Assert.Equal(credential.ApplicationId, evidenceStore.Identity.ApplicationId);
        var acknowledgment = Assert.IsAssignableFrom<IValueHttpResult>(accepted).Value;
        Assert.Contains("11111111-1111-1111-1111-111111111111", System.Text.Json.JsonSerializer.Serialize(acknowledgment));
        using (var http = new HttpClient(new SenderHandler(async message =>
        {
            var received = Context(await message.Content!.ReadAsStringAsync());
            received.Request.Headers.Authorization = message.Headers.Authorization?.ToString();
            received.Request.ContentType = message.Content.Headers.ContentType?.ToString();
            var result = await CloudHostPrivacyEvidenceEndpoints.HandleAsync(received, trust, true, CancellationToken.None);
            var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
            return new HttpResponseMessage((System.Net.HttpStatusCode)Status(result)!)
            { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        })))
        using (var sink = new OpenConnect.Privacy.CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), material.Secret))
        {
            var scope = new OpenConnect.Privacy.EgressContext(new(OpenConnect.Abstractions.Ulid.Parse("00000000000000000000000001")),
                new(OpenConnect.Abstractions.Ulid.NewUlid()), new(OpenConnect.Abstractions.Ulid.NewUlid()), OpenConnect.Privacy.EgressDestination.Output);
            var evidence = OpenConnect.Privacy.HostPrivacyEvidence.Create(Guid.NewGuid(), scope,
                new(false, null, [new(0, 5, "EMAIL")], OpenConnect.Abstractions.SecurityPolicyAction.Block, "reason"));
            await sink.RecordAsync(evidence);
            var firewall = new OpenConnect.Privacy.EvidenceRecordingPrivacyFirewall(new FixtureFirewall(), sink);
            await firewall.EvaluateAsync("private request", scope with { Destination = OpenConnect.Privacy.EgressDestination.ExternalModel });
            await firewall.EvaluateAsync("private response", scope);
            Assert.Contains(evidenceStore.Reports, report => report.Destination == OpenConnect.Privacy.EgressDestination.ExternalModel);
            Assert.Contains(evidenceStore.Reports, report => report.Destination == OpenConnect.Privacy.EgressDestination.Output && report.Allowed);
            Assert.DoesNotContain(evidenceStore.Reports, report => report.CategoryCounts.Contains("private", StringComparison.Ordinal));
            var actualFirewall = new OpenConnect.Privacy.PrivacyFirewall(
                Microsoft.Extensions.Options.Options.Create(new OpenConnect.Privacy.PrivacyOptions()),
                new OpenConnect.Privacy.Policy.PolicyEngine(new OpenConnect.Privacy.Rules.RulesSecurityDetector()));
            var recorded = new OpenConnect.Privacy.EvidenceRecordingPrivacyFirewall(actualFirewall, sink);
            var requestDecision = await recorded.EvaluateAsync("Contact request@example.com from 203.0.113.42", scope with { Destination = OpenConnect.Privacy.EgressDestination.ExternalModel });
            var responseDecision = await recorded.EvaluateAsync("Contact response@example.com, reference 123-45-6789", scope);
            Assert.DoesNotContain("request@example.com", requestDecision.SanitizedContent!, StringComparison.Ordinal);
            Assert.DoesNotContain("response@example.com", responseDecision.SanitizedContent!, StringComparison.Ordinal);
            Assert.DoesNotContain("203.0.113.42", requestDecision.SanitizedContent!, StringComparison.Ordinal);
            Assert.DoesNotContain("123-45-6789", responseDecision.SanitizedContent!, StringComparison.Ordinal);
            Assert.Contains(evidenceStore.Reports, report => report.Destination == OpenConnect.Privacy.EgressDestination.ExternalModel &&
                report.CategoryCounts.Contains("\"EMAIL\":1", StringComparison.Ordinal));
            Assert.Contains(evidenceStore.Reports, report => report.Destination == OpenConnect.Privacy.EgressDestination.Output &&
                report.CategoryCounts.Contains("\"EMAIL\":1", StringComparison.Ordinal));
            Assert.Contains(evidenceStore.Reports, report => report.Destination == OpenConnect.Privacy.EgressDestination.ExternalModel &&
                report.CategoryCounts.Contains("\"PERSONAL_DATA\":1", StringComparison.Ordinal));
            Assert.Contains(evidenceStore.Reports, report => report.Destination == OpenConnect.Privacy.EgressDestination.Output &&
                report.CategoryCounts.Contains("\"PERSONAL_DATA\":1", StringComparison.Ordinal));
            Assert.DoesNotContain("@example.com", System.Text.Json.JsonSerializer.Serialize(evidenceStore.Reports), StringComparison.Ordinal);
            Assert.DoesNotContain("203.0.113.42", System.Text.Json.JsonSerializer.Serialize(evidenceStore.Reports), StringComparison.Ordinal);
            Assert.DoesNotContain("123-45-6789", System.Text.Json.JsonSerializer.Serialize(evidenceStore.Reports), StringComparison.Ordinal);
            using var chat = new OpenConnect.Agents.Execution.FirewallGuardedChatClient(new UncalledChat(),
                new OpenConnect.Privacy.Gates.EgressGate(recorded),
                new OpenConnect.Agents.Execution.FixedEgressContextProvider(scope with { Destination = OpenConnect.Privacy.EgressDestination.ExternalModel }));
            var first = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "alice@")]);
            var second = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", "example.com")]);
            await Assert.ThrowsAsync<OpenConnect.Privacy.PrivacyBlockedException>(() =>
                chat.GetResponseAsync([new ChatMessage(ChatRole.User, "request"), first, second]));
            Assert.Contains(evidenceStore.Reports, report => !report.Allowed && report.Action == OpenConnect.Abstractions.SecurityPolicyAction.Block &&
                report.ReasonCode == "sensitive_detected" && report.Destination == OpenConnect.Privacy.EgressDestination.ExternalModel &&
                report.CategoryCounts.Contains("\"EMAIL\":1", StringComparison.Ordinal));
            Assert.DoesNotContain("alice@", System.Text.Json.JsonSerializer.Serialize(evidenceStore.Reports), StringComparison.Ordinal);
        }
        evidenceStore.Accept = false;
        Assert.Equal(409, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(Context(valid), trust, true, CancellationToken.None)));
        evidenceStore.Fail = true;
        Assert.Equal(503, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(Context(valid), trust, true, CancellationToken.None)));
        credential.RevokedAt = DateTimeOffset.UtcNow;
        Assert.Equal(401, Status(await CloudHostPrivacyEvidenceEndpoints.HandleAsync(Context("{}"), trust, true, CancellationToken.None)));
    }

    private sealed class SenderHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class UncalledChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Model must not receive blocked content.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class EvidenceStore : ICloudHostPrivacyEvidenceStore
    {
        public int Calls { get; private set; }
        public bool Accept { get; set; } = true;
        public bool Fail { get; set; }
        public CloudCredentialIdentity? Identity { get; private set; }
        public List<CloudHostPrivacyReport> Reports { get; } = [];
        public Task<bool> AppendAsync(CloudCredentialIdentity identity, CloudHostPrivacyReport report, CancellationToken cancellationToken)
        {
            Calls++;
            Identity = identity;
            if (Fail)
                throw new InvalidOperationException("sensitive storage details");
            Reports.Add(report);
            return Task.FromResult(Accept);
        }
    }

    private sealed class FixtureFirewall : OpenConnect.Privacy.IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<OpenConnect.Privacy.EgressDecision> EvaluateAsync(string content, OpenConnect.Privacy.EgressContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new OpenConnect.Privacy.EgressDecision(
                true, "masked", [new(0, 7, "PERSONAL_DATA")], OpenConnect.Abstractions.SecurityPolicyAction.Mask, "private reason"));
    }

    private sealed class CredentialStore(CloudCredential credential) : ICloudCredentialStore, ICloudCredentialSession
    {
        public Task<ICloudCredentialSession> OpenAsync(bool transactional, CancellationToken cancellationToken) => Task.FromResult<ICloudCredentialSession>(this);
        public Task<CloudCredential?> FindAsync(Guid credentialId, Guid? tenantId, Guid? applicationId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudCredential?>(credentialId == credential.Id ? credential : null);
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

    private static int? Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
}
