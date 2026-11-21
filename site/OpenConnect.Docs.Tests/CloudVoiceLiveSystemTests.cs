using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoiceLiveFactAttribute : FactAttribute
{
    public CloudVoiceLiveFactAttribute()
    {
        if (new[] { "OPENCONNECT_CLOUD_TEST_DATABASE", "OPENCONNECT_TEST_KOKORO_MODEL_DIR", "OPENCONNECT_TEST_VOICE_SCREEN_CACHE" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Requires a disposable Cloud database and explicitly provisioned Kokoro and transcription models.";
    }
}

[Collection("Cloud PostgreSQL")]
public sealed class CloudVoiceLiveSystemTests
{
    [CloudVoiceLiveFact]
    public async Task GatewaySynthesizesScreensAndSettlesRealAudio()
    {
        var root = FindRepositoryRoot();
        var kokoro = Required("OPENCONNECT_TEST_KOKORO_MODEL_DIR");
        var screen = Required("OPENCONNECT_TEST_VOICE_SCREEN_CACHE");
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(screen, "model-manifest.json")));
        var modelId = manifest.RootElement.GetProperty("modelId").GetString()!;
        var revision = manifest.RootElement.GetProperty("immutableRevision").GetString()!;
        var activePath = Directory.GetFiles(Path.Combine(screen, "transcription"), "active.json", SearchOption.AllDirectories).Single();
        using var active = JsonDocument.Parse(await File.ReadAllTextAsync(activePath));
        var screenFingerprint = active.RootElement.GetProperty("activeFingerprint").GetString()!;
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Required("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Voice acceptance", default);
        var application = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Voice test", "Development", default)).Application!;
        var deploymentId = Guid.NewGuid(); var endpointRef = "voice-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow; var modelDigest = "sha256:496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4";
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "voice.synthesize");
            capability.PlatformAvailability = "available"; capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "voice.synthesize", EndpointReference = endpointRef,
                Transport = "https-mtls", Region = "region-a", ContractVersion = "v1", ModelId = "kokoro", ModelRevision = "v1",
                ModelDigest = modelDigest, ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "voice.synthesize", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "voice.synthesize", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = application.Id, CapabilityId = "voice.synthesize", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = application.Id,
                CapabilityId = "voice.synthesize", Unit = "character", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(15), Limit = 1000 });
            await db.SaveChangesAsync();
        }
        var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
        var credential = await credentials.CreateAsync(owner, tenant.Id, application.Id, ["voice.synthesize"], now.AddMinutes(10), default);
        Assert.Equal("succeeded", credential.Status);

        using var authorityCert = Certificate(false); using var workerServer = Certificate(false); using var screenServer = Certificate(false);
        using var gatewayCert = Certificate(true); using var workerCert = Certificate(true);
        var gatewayTrust = new CloudGatewayClientTrust([Convert.ToHexString(gatewayCert.GetCertHash(HashAlgorithmName.SHA256))]);
        var workerTrust = new CloudWorkerClientTrust([new(deploymentId, Convert.ToHexString(workerCert.GetCertHash(HashAlgorithmName.SHA256)))], gatewayTrust);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(authorityCert, https =>
        { https.ClientCertificateMode = ClientCertificateMode.AllowCertificate; https.ClientCertificateValidation = (_, _, _) => true; })));
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>(); builder.Services.AddScoped<CloudCredentialService>();
        builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>(); builder.Services.AddScoped<CloudMachineAuthorizationService>();
        builder.Services.AddScoped<ICloudQuotaStore, EfCloudQuotaStore>(); builder.Services.AddScoped<CloudQuotaService>();
        builder.Services.AddScoped<CloudReadinessService>(); builder.Services.AddCloudVoiceFeature();
        await using var host = builder.Build();
        host.MapCloudGatewayReadiness(gatewayTrust, true); host.MapCloudGatewayAuthorization(gatewayTrust, true);
        host.MapCloudVoiceGatewayEndpoints(gatewayTrust, true); host.MapCloudVoiceWorkerEndpoints(workerTrust, true);
        await host.StartAsync();
        var authorityPort = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        var workerPort = FreePort(); var screenPort = FreePort(); var gatewayPort = FreePort();
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.GetFullPath(Path.Combine(temporaryRoot, "oc-voice-live-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        Directory.CreateDirectory(directory);
        var processes = new List<Process>();
        try
        {
            WriteCertificate(directory, "authority", authorityCert); WriteCertificate(directory, "worker", workerServer);
            WriteCertificate(directory, "screen", screenServer); WriteCertificate(directory, "gateway", gatewayCert); WriteCertificate(directory, "worker-client", workerCert);
            var tokenFile = Path.Combine(directory, "token"); await File.WriteAllTextAsync(tokenFile, "test-worker-token\n");
            var fingerprintFile = Path.Combine(directory, "fingerprints"); await File.WriteAllTextAsync(fingerprintFile, "key:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
            var authority = $"https://127.0.0.1:{authorityPort}";
            var commonTls = new[] { "--host", "127.0.0.1", "--ssl-ca-certs", Path.Combine(directory, "gateway.crt"), "--ssl-cert-reqs", "2", "--no-access-log" };
            var workerEnv = new Dictionary<string, string> { ["PYTHONPATH"] = Path.Combine(root, "workers", "kokoro", "src"),
                ["OPENCONNECT_WORKER_TOKEN"] = "test-worker-token", ["OC_MODEL_CACHE_DIR"] = kokoro, ["OC_KOKORO_ALLOW_DOWNLOAD"] = "false",
                ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1",
                ["OPENCONNECT_CLOUD_CONSUMPTION_ENABLED"] = "true", ["OPENCONNECT_CLOUD_DEPLOYMENT_ID"] = deploymentId.ToString("D"),
                ["OPENCONNECT_CLOUD_AUTHORITY_ORIGIN"] = authority, ["OPENCONNECT_CLOUD_AUTHORITY_CA_FILE"] = Path.Combine(directory, "authority.crt"),
                ["OPENCONNECT_CLOUD_WORKER_CERT_FILE"] = Path.Combine(directory, "worker-client.crt"), ["OPENCONNECT_CLOUD_WORKER_KEY_FILE"] = Path.Combine(directory, "worker-client.key") };
            var workerPython = Python(root, "kokoro");
            processes.Add(Start(workerPython, root, ["-m", "uvicorn", "openconnect_kokoro.app:app", .. commonTls, "--port", workerPort.ToString(),
                "--ssl-certfile", Path.Combine(directory, "worker.crt"), "--ssl-keyfile", Path.Combine(directory, "worker.key")], workerEnv));
            var screenEnv = new Dictionary<string, string> { ["PYTHONPATH"] = Path.Combine(root, "workers", "transcription", "src"),
                ["OPENCONNECT_WORKER_TOKEN"] = "test-worker-token", ["TRANSCRIPTION_BACKEND"] = "local-model", ["OC_WORKER_ENGINE"] = "real",
                ["OC_MODEL_DEVICE"] = "cpu", ["OC_MODEL_CACHE_DIR"] = screen, ["OC_TRANSCRIPTION_MODEL"] = modelId,
                ["TRANSCRIPTION_MODEL_REVISION"] = revision, ["OC_TRANSCRIPTION_FILE_URL"] = "", ["TRANSCRIPTION_CLOUD_URL"] = "",
                ["TRANSCRIPTION_MAX_AUDIO_BYTES"] = "5760044", ["TRANSCRIPTION_MAX_DURATION_MS"] = "120000",
                ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1" };
            processes.Add(Start(Python(root, "transcription"), root, ["-m", "uvicorn", "openconnect_transcription.app:app", .. commonTls, "--port", screenPort.ToString(),
                "--ssl-certfile", Path.Combine(directory, "screen.crt"), "--ssl-keyfile", Path.Combine(directory, "screen.key")], screenEnv));
            var gatewayEnv = new Dictionary<string, string> { ["PORT"] = gatewayPort.ToString(), ["CLOUD_AUTHORITY_ORIGIN"] = authority,
                ["CLOUD_AUTHORITY_CA_FILE"] = Path.Combine(directory, "authority.crt"), ["CLOUD_AUTHORITY_CERT_FILE"] = Path.Combine(directory, "gateway.crt"),
                ["CLOUD_AUTHORITY_KEY_FILE"] = Path.Combine(directory, "gateway.key"), ["CLOUD_FINGERPRINT_ACTIVE_KEY_ID"] = "key", ["CLOUD_FINGERPRINT_KEYS_FILE"] = fingerprintFile,
                ["CLOUD_VOICE_EXECUTION_ENABLED"] = "true", ["CLOUD_VOICE_ORIGIN"] = $"https://127.0.0.1:{workerPort}",
                ["CLOUD_VOICE_CA_FILE"] = Path.Combine(directory, "worker.crt"), ["CLOUD_VOICE_CERT_FILE"] = Path.Combine(directory, "gateway.crt"),
                ["CLOUD_VOICE_KEY_FILE"] = Path.Combine(directory, "gateway.key"), ["CLOUD_VOICE_TOKEN_FILE"] = tokenFile,
                ["CLOUD_VOICE_DEPLOYMENT_ID"] = deploymentId.ToString("D"), ["CLOUD_VOICE_ENDPOINT_REFERENCE"] = endpointRef,
                ["CLOUD_VOICE_REGION"] = "region-a", ["CLOUD_VOICE_CONTRACT_VERSION"] = "v1", ["CLOUD_VOICE_MODEL_ID"] = "kokoro",
                ["CLOUD_VOICE_MODEL_REVISION"] = "v1", ["CLOUD_VOICE_MODEL_DIGEST"] = modelDigest,
                ["CLOUD_VOICE_SCREEN_ORIGIN"] = $"https://127.0.0.1:{screenPort}", ["CLOUD_VOICE_SCREEN_CA_FILE"] = Path.Combine(directory, "screen.crt"),
                ["CLOUD_VOICE_SCREEN_CERT_FILE"] = Path.Combine(directory, "gateway.crt"), ["CLOUD_VOICE_SCREEN_KEY_FILE"] = Path.Combine(directory, "gateway.key"),
                ["CLOUD_VOICE_SCREEN_TOKEN_FILE"] = tokenFile, ["CLOUD_VOICE_SCREEN_MODEL_ID"] = modelId,
                ["CLOUD_VOICE_SCREEN_MODEL_REVISION"] = revision, ["CLOUD_VOICE_SCREEN_MODEL_FINGERPRINT"] = screenFingerprint };
            processes.Add(Start("node", root, [Path.Combine(root, "cloud", "gateway", "start.mjs")], gatewayEnv));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gatewayPort}"), Timeout = TimeSpan.FromSeconds(120) };
            var ready = false;
            for (var attempt = 0; attempt < 45 && !ready; attempt++)
            {
                try { using var response = await http.GetAsync("/health/ready"); ready = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (!ready) await Task.Delay(1000);
            }
            Assert.True(ready, "Voice gateway, synthesis worker and audio screen did not become ready.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
            var requestId = Guid.NewGuid();
            var request = new { schemaVersion = "openconnect.cloud.voice-synthesis/v1", requestId, capability = "voice.synthesize",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(3).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                input = new { text = "Hello world.", voiceId = "af_heart", locale = "en-US", format = "pcm" } };
            using var result = await http.PostAsJsonAsync("/cloud/v1/voice/synthesis", request);
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Assert.Equal(requestId.ToString("D"), json.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("openconnect.cloud.voice-synthesis.result/v1", json.RootElement.GetProperty("schemaVersion").GetString());
            var audio = json.RootElement.GetProperty("result");
            Assert.Equal("pcm", audio.GetProperty("format").GetString());
            Assert.Equal(24000, audio.GetProperty("sampleRateHz").GetInt32());
            Assert.False(audio.TryGetProperty("text", out _));
            Assert.NotEmpty(Convert.FromBase64String(audio.GetProperty("audioBase64").GetString()!));
            await using var verify = await factory.CreateDbContextAsync();
            var reservation = await verify.QuotaReservations.SingleAsync(x => x.RequestId == requestId && x.TenantId == tenant.Id);
            Assert.NotNull(reservation.FinalizedAt); Assert.Equal(request.input.text.Length, reservation.Actual);
            Assert.Equal(1, await verify.ExecutionConsumptions.CountAsync(x => x.ReservationId == reservation.Id));
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.voice_output_privacy_evidence WHERE \"ReservationId\" = {reservation.Id} AND \"Outcome\" = 'allowed'").SingleAsync());
            using var replay = await http.PostAsJsonAsync("/cloud/v1/voice/synthesis", request);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            var blockedId = Guid.NewGuid();
            var blockedRequest = new { schemaVersion = "openconnect.cloud.voice-synthesis/v1", requestId = blockedId, capability = "voice.synthesize",
                expiresAt = request.expiresAt, input = new { text = "person@example.test", voiceId = "af_heart", locale = "en-US", format = "pcm" } };
            using var blocked = await http.PostAsJsonAsync("/cloud/v1/voice/synthesis", blockedRequest);
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.False(await verify.QuotaReservations.AnyAsync(x => x.RequestId == blockedId && x.TenantId == tenant.Id));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-credential");
            using var unauthorized = await http.PostAsJsonAsync("/cloud/v1/voice/synthesis", blockedRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
            Directory.Delete(directory, recursive: true);
            manifest.Dispose();
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required.");
    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "OpenConnect.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
    private static string Python(string root, string worker) => Path.Combine(root, "workers", worker, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static Process Start(string file, string workingDirectory, string[] arguments, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(key => key == "PORT" || key.StartsWith("CLOUD_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("OPENCONNECT_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OC_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("TRANSCRIPTION_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("KOKORO_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var item in environment) start.Environment[item.Key] = item.Value;
        return Process.Start(start) ?? throw new InvalidOperationException("Test process could not start.");
    }
    private static X509Certificate2 Certificate(bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client) { var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build()); }
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    private static void WriteCertificate(string directory, string name, X509Certificate2 certificate)
    {
        File.WriteAllText(Path.Combine(directory, name + ".crt"), certificate.ExportCertificatePem());
        using var key = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(Path.Combine(directory, name + ".key"), key.ExportPkcs8PrivateKeyPem());
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    { public CloudControlPlaneDbContext CreateDbContext() => new(options); }
}
