using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudKnowledgeEmbeddingConsumptionTests
{
    [Fact]
    public async Task RequiresTrustedWorkerAndConsumesOnlyBoundProof()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=worker-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var deployment = Guid.NewGuid();
        var trust = new CloudWorkerClientTrust([new(deployment, Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)))], null);
        var store = new Store();
        using var services = new ServiceCollection().AddSingleton<ICloudKnowledgeEmbeddingConsumptionStore>(store).BuildServiceProvider();
        DefaultHttpContext Context()
        {
            var value = new DefaultHttpContext { RequestServices = services };
            value.Request.Scheme = "https"; value.Connection.ClientCertificate = certificate;
            value.Request.Headers["X-OpenConnect-Worker-Payload-Fingerprint"] = Convert.ToBase64String(new byte[32]);
            return value;
        }
        var id = Guid.NewGuid();
        var untrusted = Context(); untrusted.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudKnowledgeEmbeddingConsumptionEndpoints.ConsumeAsync(id, id, untrusted, trust, true, CancellationToken.None)));
        Assert.Equal(503, Status(await CloudKnowledgeEmbeddingConsumptionEndpoints.ConsumeAsync(id, id, Context(), trust, false, CancellationToken.None)));
        var body = Context(); body.Request.ContentLength = 1;
        Assert.Equal(400, Status(await CloudKnowledgeEmbeddingConsumptionEndpoints.ConsumeAsync(id, id, body, trust, true, CancellationToken.None)));
        var malformed = Context(); malformed.Request.Headers["X-OpenConnect-Worker-Payload-Fingerprint"] = "bad";
        Assert.Equal(400, Status(await CloudKnowledgeEmbeddingConsumptionEndpoints.ConsumeAsync(id, id, malformed, trust, true, CancellationToken.None)));
        Assert.Equal(0, store.Calls);
        var accepted = Context();
        Assert.Equal(200, Status(await CloudKnowledgeEmbeddingConsumptionEndpoints.ConsumeAsync(id, id, accepted, trust, true, CancellationToken.None)));
        Assert.Equal(deployment, store.Deployment);
        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.All(store.Fingerprint!, value => Assert.Equal(0, value));
        store.Status = "already_consumed";
        Assert.Equal(409, Status(await CloudKnowledgeEmbeddingConsumptionEndpoints.ConsumeAsync(id, id, Context(), trust, true, CancellationToken.None)));
    }

    private static int? Status(IResult value) => (value as IStatusCodeHttpResult)?.StatusCode;
    private sealed class Store : ICloudKnowledgeEmbeddingConsumptionStore
    {
        public int Calls { get; private set; }
        public Guid Deployment { get; private set; }
        public byte[]? Fingerprint { get; private set; }
        public string Status { get; set; } = "consumed";
        public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] fingerprint, CancellationToken cancellationToken)
        { Calls++; Deployment = deploymentId; Fingerprint = fingerprint; return Task.FromResult(Status); }
    }
}
