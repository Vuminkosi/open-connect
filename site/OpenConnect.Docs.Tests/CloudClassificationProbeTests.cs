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

public sealed class CloudClassificationWorkerFactAttribute : FactAttribute
{
    public CloudClassificationWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLASSIFICATION_TEST_PYTHON")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_CLASSIFICATION_WORKER_ROOT")))
            Skip = "Set the disposable classification worker Python and source paths to run the real-worker probe.";
    }
}

public sealed class CloudClassificationProbeTests
{
    [CloudClassificationWorkerFact]
    public async Task ActualFakeClassificationWorkerIsRejectedThroughMutualTlsProxy()
    {
        var portListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var workerPort = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("OPENCONNECT_CLASSIFICATION_TEST_PYTHON")!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "-m", "uvicorn", "openconnect_classification.app:app", "--host", "127.0.0.1", "--port", workerPort.ToString(), "--log-level", "warning" })
            start.ArgumentList.Add(value);
        start.Environment["PYTHONPATH"] = Path.Combine(Environment.GetEnvironmentVariable("OPENCONNECT_CLASSIFICATION_WORKER_ROOT")!, "src");
        start.Environment["OC_ENVIRONMENT"] = "Development";
        start.Environment["OC_WORKER_ENGINE"] = "fake";
        start.Environment["OC_MODEL_PROVISIONING"] = "disabled";
        start.Environment["OC_MODEL_OFFLINE"] = "true";
        start.Environment["OC_MODEL_DEVICE"] = "cpu";
        start.Environment["OPENCONNECT_WORKER_TOKEN"] = "worker-secret";
        using var worker = Process.Start(start)!;
        try
        {
            using var workerClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{workerPort}") };
            var ready = false;
            for (var attempt = 0; attempt < 100 && !ready; attempt++)
            {
                if (worker.HasExited) throw new InvalidOperationException("Classification worker exited before readiness.");
                try { ready = (await workerClient.GetAsync("/health/live")).IsSuccessStatusCode; }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Assert.True(ready);
            Assert.Equal(HttpStatusCode.Unauthorized, (await workerClient.GetAsync("/internal/v1/capabilities")).StatusCode);
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/internal/v1/capabilities"))
            {
                request.Headers.Add("Authorization", "Bearer worker-secret");
                using var response = await workerClient.SendAsync(request);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("classification", json.RootElement.GetProperty("workerKind").GetString());
                Assert.Equal("fake", json.RootElement.GetProperty("engine").GetString());
            }
            using var serverCertificate = Certificate(false);
            using var clientCertificate = Certificate(true);
            var trust = new CloudGatewayClientTrust([Convert.ToHexString(clientCertificate.GetCertHash(HashAlgorithmName.SHA256))]);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(serverCertificate, trust.ConfigureHttps)));
            await using var app = builder.Build();
            async Task<IResult> Forward(HttpContext context)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Request.Path.Value);
                if (context.Request.Headers.TryGetValue("Authorization", out var authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
                using var response = await workerClient.SendAsync(request);
                return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
            }
            app.MapGet("/health/ready", (Delegate)Forward);
            app.MapGet("/internal/v1/capabilities", (Delegate)Forward);
            var forwardedExecutions = 0;
            app.MapPost("/internal/v1/execute", async (HttpContext context) =>
            {
                Interlocked.Increment(ref forwardedExecutions);
                using var request = new HttpRequestMessage(HttpMethod.Post, context.Request.Path.Value);
                if (context.Request.Headers.TryGetValue("Authorization", out var authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await workerClient.SendAsync(request);
                return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
            });
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret", "fake-0.1.0"));
            Assert.Equal("upstream_unavailable", await DispatchAsync(address, serverCertificate, clientCertificate, "worker-secret", "fake-0.1.0"));
            Assert.Equal(1, forwardedExecutions);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
        }
    }

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
        app.MapGet("/internal/cloud/v1/classification/ready", (HttpContext context) => context.Request.Headers.Authorization == "Bearer worker-secret" && mode != "cloud-down"
            ? Results.Json(new { status = "ready", deploymentId = mode == "wrong-deployment" ? "22222222-2222-2222-2222-222222222222" : "11111111-1111-1111-1111-111111111111" }) : Results.StatusCode(503));
        app.MapPost("/internal/v1/execute", async (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization != "Bearer worker-secret") return Results.Unauthorized();
            using var json = await JsonDocument.ParseAsync(context.Request.Body);
            var taskId = json.RootElement.GetProperty("taskId").GetString();
            return Results.Json(new { schemaVersion = "openconnect.v1", taskId, status = "completed", error = (string?)null,
                result = new { labels = new[] { "safe" }, scores = new { safe = 0.9 }, abstained = false,
                    modelRevision = mode == "revision" ? "other" : "revision-1", labelSetVersion = "v1", engine = mode == "fake" ? "fake" : "real", engineWarning = (string?)null, grantsTools = false } });
        });
        var receivedClaimed = false;
        app.MapPost("/internal/cloud/v1/classification/execute", async (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization != "Bearer worker-secret") return Results.Unauthorized();
            using var envelope = await JsonDocument.ParseAsync(context.Request.Body);
            var root = envelope.RootElement;
            Assert.Equal("openconnect.cloud.worker.classification/v1", root.GetProperty("schemaVersion").GetString());
            Assert.Equal("22222222-2222-2222-2222-222222222222", root.GetProperty("reservationId").GetString());
            Assert.Equal("33333333-3333-3333-3333-333333333333", root.GetProperty("claimId").GetString());
            Assert.Equal(32, Convert.FromBase64String(root.GetProperty("key").GetString()!).Length);
            using var payload = JsonDocument.Parse(Convert.FromBase64String(root.GetProperty("payload").GetString()!));
            var taskId = payload.RootElement.GetProperty("taskId").GetString();
            Assert.Equal("test input", payload.RootElement.GetProperty("input").GetProperty("text").GetString());
            receivedClaimed = true;
            return Results.Json(new { schemaVersion = "openconnect.v1", taskId, status = "completed", error = (string?)null,
                result = new { labels = new[] { "safe" }, scores = new { safe = 0.9 }, abstained = false,
                    modelRevision = "revision-1", labelSetVersion = "v1", engine = mode == "fake" ? "fake" : "real", engineWarning = (string?)null, grantsTools = false } });
        });
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        Assert.True(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        Assert.True(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret", requireCloud: true));
        mode = "wrong-deployment";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret", requireCloud: true));
        mode = "cloud-down";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret", requireCloud: true));
        mode = "real";
        Assert.Equal("completed", await DispatchAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        Assert.Equal("completed", await ClaimedDispatchAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        Assert.True(receivedClaimed);
        mode = "fake";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        Assert.Equal("upstream_unavailable", await DispatchAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        Assert.Equal("upstream_unavailable", await ClaimedDispatchAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "revision";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "unadvertised";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "worker-secret"));
        mode = "real";
        Assert.False(await ProbeAsync(address, serverCertificate, clientCertificate, "wrong-secret"));
        Assert.False(await ProbeAsync(address, serverCertificate, wrongCertificate, "worker-secret"));
        Assert.False(await ProbeAsync(address, wrongCertificate, clientCertificate, "worker-secret"));
        Assert.Equal("upstream_unavailable", await DispatchAsync(address, serverCertificate, clientCertificate, "wrong-secret"));
        Assert.Equal("upstream_unavailable", await DispatchAsync(address, serverCertificate, wrongCertificate, "worker-secret"));
        Assert.Equal("upstream_unavailable", await DispatchAsync(address, wrongCertificate, clientCertificate, "worker-secret"));
    }

    private static async Task<string> ClaimedDispatchAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string token)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE); const b=await import(new URL('./classification-payload.mjs',process.env.OC_TEST_MODULE)); const p=m.createClassificationDispatcher({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY,workerToken:process.env.OC_TEST_TOKEN,modelRevision:'revision-1'}); const prepared=m.prepareClassificationWorkerPayload({requestId:'11111111-1111-1111-1111-111111111111',input:{text:'test input'}}); const bound=b.bindClassificationPayload(prepared.payload); try {process.stdout.write(JSON.stringify(await p.executeClaimed(prepared,{reservationId:'22222222-2222-2222-2222-222222222222',claimId:'33333333-3333-3333-3333-333333333333',key:bound.key})));} finally {prepared.payload.fill(0);bound.key.fill(0);bound.fingerprint.fill(0);}";
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "classification-dispatch.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString(); start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = client.ExportCertificatePem();
        using var key = client.GetRSAPrivateKey()!; start.Environment["OC_TEST_KEY"] = key.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_TOKEN"] = token;
        using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, errors);
        using var json = JsonDocument.Parse(output); return json.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<string> DispatchAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string token, string revision = "revision-1")
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE); const p=m.createClassificationDispatcher({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY,workerToken:process.env.OC_TEST_TOKEN,modelRevision:process.env.OC_TEST_REVISION}); process.stdout.write(JSON.stringify(await p.execute({requestId:'11111111-1111-1111-1111-111111111111',input:{text:'test input'}})));";
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "classification-dispatch.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString(); start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = client.ExportCertificatePem();
        using var key = client.GetRSAPrivateKey()!; start.Environment["OC_TEST_KEY"] = key.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_TOKEN"] = token; start.Environment["OC_TEST_REVISION"] = revision;
        using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, errors);
        using var json = JsonDocument.Parse(output); return json.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<bool> ProbeAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string token, string revision = "revision-1", bool requireCloud = false)
    {
        const string script = "const m=await import(process.env.OC_TEST_MODULE); const p=m.createClassificationProbe({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY,workerToken:process.env.OC_TEST_TOKEN,modelRevision:process.env.OC_TEST_REVISION}); process.stdout.write(JSON.stringify(await p.probe(undefined,undefined,process.env.OC_TEST_REQUIRE_CLOUD==='true','11111111-1111-1111-1111-111111111111')));";
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "classification-probe.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = client.ExportCertificatePem();
        using var key = client.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = key.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_TOKEN"] = token;
        start.Environment["OC_TEST_REVISION"] = revision;
        start.Environment["OC_TEST_REQUIRE_CLOUD"] = requireCloud ? "true" : "false";
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
