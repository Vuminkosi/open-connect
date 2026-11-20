using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudClassificationProbeTests
{
    [Fact]
    public async Task ProbeRequiresPrivateTlsHealthyRealEngineAndMatchingAdvertisement()
    {
        using var serverCertificate = Certificate(false);
        using var clientCertificate = Certificate(true);
        using var wrongCertificate = Certificate(true);
        var trust = new CloudGatewayClientTrust([Convert.ToHexString(clientCertificate.GetCertHash(HashAlgorithmName.SHA256))]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(serverCertificate, trust.ConfigureHttps)));
        await using var app = builder.Build();
        var mode = "real";
        app.MapGet("/health/ready", () => Results.Json(new { status = "ready", ready = true, engine = mode == "fake" ? "fake" : "real", modelRevision = "revision-1" }));
        app.MapGet("/internal/v1/capabilities", (HttpContext context) => context.Request.Headers.Authorization == "Bearer worker-secret"
            ? Results.Json(new { workerKind = "classification", engine = "real", modelRevision = mode == "revision" ? "other" : "revision-1",
                capabilities = mode == "unadvertised" ? Array.Empty<object>() : [new { kind = "classification", grantsTools = false }] })
            : Results.Unauthorized());
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        Assert.True(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "fake";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "revision";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "unadvertised";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "real";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "wrong-secret"));
        Assert.False(await ProbeAsync(address, serverCertificate, wrongCertificate, "worker-secret"));
        Assert.False(await ProbeAsync(address, wrongCertificate, clientCertificate, "worker-secret"));
    }

    private static async Task<bool> ProbeAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string token)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE); const p=m.createClassificationProbe({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY,workerToken:process.env.OC_TEST_TOKEN,modelRevision:'revision-1'}); process.stdout.write(JSON.stringify(await p.probe()));";
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "classification-probe.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = client.ExportCertificatePem();
        using var key = client.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = key.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_TOKEN"] = token;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("ready").GetBoolean();
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
}
