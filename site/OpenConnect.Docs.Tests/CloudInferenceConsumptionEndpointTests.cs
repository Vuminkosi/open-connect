using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudInferenceConsumptionEndpointTests
{
    [Fact]
    public async Task ConsumptionRequiresPinnedWorkerCertificateAndValidProof()
    {
        using var server = Certificate(false);
        using var worker = Certificate(true);
        using var other = Certificate(true);
        var deployment = Guid.NewGuid();
        var trust = new CloudWorkerClientTrust([new(deployment, Convert.ToHexString(worker.GetCertHash(HashAlgorithmName.SHA256)))], null);
        var store = new RecordingStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(server, https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (_, _, _) => true;
        })));
        builder.Services.AddSingleton<ICloudInferenceConsumptionStore>(store);
        builder.Services.AddSingleton<CloudInferenceConsumptionService>();
        await using var app = builder.Build();
        app.MapCloudInferenceConsumption(trust, true);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var anonymous = Client(address, server, null);
        using var unauthorized = Client(address, server, other);
        using var authorized = Client(address, server, worker);
        var path = $"/cloud/internal/v1/inference/quota/reservations/{Guid.NewGuid():D}/claims/{Guid.NewGuid():D}/consume";
        async Task<HttpResponseMessage> Send(HttpClient client, string? proof)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (proof is not null) request.Headers.Add("X-OpenConnect-Worker-Payload-Fingerprint", proof);
            return await client.SendAsync(request);
        }
        var fingerprint = Convert.ToBase64String(new byte[32]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous, fingerprint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(unauthorized, fingerprint)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(authorized, "invalid")).StatusCode);
        Assert.Equal(0, store.Calls);
        var response = await Send(authorized, fingerprint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(deployment, store.DeploymentId);
        Assert.Equal(1, store.Calls);
    }
    private sealed class RecordingStore : ICloudInferenceConsumptionStore
    {
        public int Calls { get; private set; }
        public Guid DeploymentId { get; private set; }
        public Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken)
        {
            Calls++; DeploymentId = deploymentId;
            return Task.FromResult("consumed");
        }
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
}
