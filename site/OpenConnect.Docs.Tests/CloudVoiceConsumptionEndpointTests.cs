using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoiceConsumptionEndpointTests
{
    [Fact]
    public async Task WorkerCertificateBindsDeploymentAndRequiresCanonicalFingerprintWithNoBody()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=worker-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var deployment = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var claim = Guid.NewGuid();
        var trust = new CloudWorkerClientTrust([new(deployment, Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)))], null);
        var store = new ConsumptionStore();
        var settlements = new SettlementStore();
        using var services = new ServiceCollection().AddSingleton<ICloudVoiceConsumptionStore>(store).AddSingleton<CloudVoiceConsumptionService>()
            .AddSingleton<ICloudVoiceSettlementStore>(settlements).AddSingleton<CloudVoiceSettlement>().BuildServiceProvider();
        DefaultHttpContext Context(string body = "")
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Scheme = "https";
            context.Connection.ClientCertificate = certificate;
            context.Request.Headers["X-OpenConnect-Worker-Payload-Fingerprint"] = Convert.ToBase64String(new byte[32]);
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return context;
        }
        var noCert = Context();
        noCert.Connection.ClientCertificate = null;
        Assert.Equal(401, Status(await CloudVoiceConsumptionEndpoints.ConsumeAsync(reservation, claim, noCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudVoiceConsumptionEndpoints.ConsumeAsync(reservation, claim, Context("x"), trust, true, default)));
        var invalid = Context();
        invalid.Request.Headers["X-OpenConnect-Worker-Payload-Fingerprint"] = "invalid";
        Assert.Equal(400, Status(await CloudVoiceConsumptionEndpoints.ConsumeAsync(reservation, claim, invalid, trust, true, default)));
        Assert.Equal(0, store.Calls);
        var accepted = Context();
        Assert.Equal(200, Status(await CloudVoiceConsumptionEndpoints.ConsumeAsync(reservation, claim, accepted, trust, true, default)));
        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.Equal((reservation, claim, deployment), store.Last);
        store.Next = "already_consumed";
        Assert.Equal(409, Status(await CloudVoiceConsumptionEndpoints.ConsumeAsync(reservation, claim, Context(), trust, true, default)));
        Assert.Equal(401, Status(await CloudVoiceSettlementEndpoints.SettleAsync(reservation, claim, noCert, trust, true, default)));
        Assert.Equal(400, Status(await CloudVoiceSettlementEndpoints.SettleAsync(reservation, claim, Context("x"), trust, true, default)));
        Assert.Equal(0, settlements.Calls);
        Assert.Equal(200, Status(await CloudVoiceSettlementEndpoints.SettleAsync(reservation, claim, Context(), trust, true, default)));
        Assert.Equal((reservation, claim, deployment), settlements.Last);
        settlements.Next = "conflict";
        Assert.Equal(409, Status(await CloudVoiceSettlementEndpoints.SettleAsync(reservation, claim, Context(), trust, true, default)));
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private sealed class ConsumptionStore : ICloudVoiceConsumptionStore
    {
        public int Calls { get; private set; }
        public string Next { get; set; } = "consumed";
        public (Guid, Guid, Guid) Last { get; private set; }
        public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerFingerprint, CancellationToken cancellationToken)
        { Calls++; Last = (reservationId, claimId, deploymentId); return Task.FromResult(Next); }
    }
    private sealed class SettlementStore : ICloudVoiceSettlementStore
    {
        public int Calls { get; private set; }
        public string Next { get; set; } = "completed";
        public (Guid, Guid, Guid) Last { get; private set; }
        public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken)
        { Calls++; Last = (reservationId, claimId, deploymentId); return Task.FromResult(Next); }
    }
}
