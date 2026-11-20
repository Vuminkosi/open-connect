using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudInferenceLiveFactAttribute : FactAttribute
{
    public CloudInferenceLiveFactAttribute()
    {
        if (new[] { "OPENCONNECT_CLOUD_TEST_DATABASE", "OPENCONNECT_TEST_GATEWAY_IMAGE", "OPENCONNECT_TEST_WORKER_IMAGE",
            "OPENCONNECT_TEST_MODEL_VOLUME", "OPENCONNECT_TEST_MODEL_REVISION", "OPENCONNECT_TEST_MODEL_DIGEST" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Requires a disposable Cloud database, test images and an already-provisioned approved model volume.";
    }
}

[Collection("Cloud PostgreSQL")]
public sealed class CloudInferenceLiveSystemTests
{
    [CloudInferenceLiveFact]
    public async Task GatewayExecutesRealModelAndRecordsSettledUsageAndPrivacy()
    {
        const string modelId = "local-qwen";
        var revision = Required("OPENCONNECT_TEST_MODEL_REVISION");
        var digest = Required("OPENCONNECT_TEST_MODEL_DIGEST");
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Required("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Cloud inference acceptance", default);
        var application = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Fixture app", "Development", default)).Application!;
        var deploymentId = Guid.NewGuid();
        var deploymentRef = "inference-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.chat");
            capability.PlatformAvailability = "available"; capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "inference.chat", EndpointReference = deploymentRef,
                Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = modelId,
                ModelRevision = revision, ModelDigest = digest, ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "inference.chat", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "inference.chat", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = application.Id, CapabilityId = "inference.chat", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = application.Id,
                CapabilityId = "inference.chat", Unit = "token", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(15), Limit = 10000 });
            await db.SaveChangesAsync();
        }
        var policy = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
        Assert.Equal("succeeded", (await policy.PutAsync(owner, tenant.Id, [modelId], 0, default)).Status);
        var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
        var credential = await credentials.CreateAsync(owner, tenant.Id, application.Id, ["inference.chat"], now.AddMinutes(10), default);
        Assert.Equal("succeeded", credential.Status);

        using var authorityCert = Certificate("host.docker.internal", false);
        using var workerServerCert = Certificate("cloud-inference-worker", false);
        using var gatewayCert = Certificate("gateway-client", true);
        using var workerCert = Certificate("worker-client", true);
        var gatewayTrust = new CloudGatewayClientTrust([Convert.ToHexString(gatewayCert.GetCertHash(HashAlgorithmName.SHA256))]);
        var workerTrust = new CloudWorkerClientTrust([new(deploymentId, Convert.ToHexString(workerCert.GetCertHash(HashAlgorithmName.SHA256)))], gatewayTrust);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, 0, endpoint => endpoint.UseHttps(authorityCert, https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (_, _, _) => true;
        })));
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>(); builder.Services.AddScoped<CloudCredentialService>();
        builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>(); builder.Services.AddScoped<CloudMachineAuthorizationService>();
        builder.Services.AddScoped<ICloudQuotaStore, EfCloudQuotaStore>(); builder.Services.AddScoped<CloudQuotaService>();
        builder.Services.AddScoped<ICloudExecutionClaimStore, EfCloudExecutionClaimStore>(); builder.Services.AddScoped<CloudExecutionClaimService>();
        builder.Services.AddScoped<ICloudInferencePrivacyEvidenceStore, EfCloudInferencePrivacyEvidenceStore>();
        builder.Services.AddSingleton(new CloudInferencePrivacyService(new RulesSecurityDetector()));
        builder.Services.AddScoped<ICloudInferenceAccessStore, EfCloudInferenceAccessStore>();
        builder.Services.AddScoped<ICloudInferenceOutputEvidenceStore, EfCloudInferenceOutputEvidenceStore>();
        builder.Services.AddSingleton(new CloudInferenceOutputPrivacyService(new RulesSecurityDetector()));
        builder.Services.AddScoped<ICloudInferenceConsumptionStore, EfCloudInferenceConsumptionStore>(); builder.Services.AddScoped<CloudInferenceConsumptionService>();
        builder.Services.AddScoped<ICloudInferenceTokenSettlementStore, EfCloudInferenceTokenSettlementStore>(); builder.Services.AddScoped<CloudInferenceTokenSettlement>();
        builder.Services.AddScoped<ICloudWorkerReadinessStore, EfCloudWorkerReadinessStore>(); builder.Services.AddScoped<CloudWorkerReadiness>();
        builder.Services.AddScoped<CloudReadinessService>();
        await using var host = builder.Build();
        host.MapCloudGatewayReadiness(gatewayTrust, true); host.MapCloudGatewayAuthorization(gatewayTrust, true);
        host.MapCloudInferencePrivacy(gatewayTrust, true); host.MapCloudInferenceFingerprintKeyLookup(gatewayTrust, true);
        host.MapCloudInferenceQuota(gatewayTrust, true); host.MapCloudInferenceClaim(gatewayTrust, true);
        host.MapCloudInferenceAccess(gatewayTrust, true); host.MapCloudInferenceOutputPrivacy(gatewayTrust, true);
        host.MapCloudWorkerReadiness(workerTrust, true); host.MapCloudInferenceConsumption(workerTrust, true); host.MapCloudInferenceTokenSettlement(workerTrust, true);
        await host.StartAsync();
        var authorityPort = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var network = "oc-inference-live-" + suffix; var worker = "oc-inference-worker-" + suffix; var gateway = "oc-inference-gateway-" + suffix;
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.GetFullPath(Path.Combine(temporaryRoot, "oc-inference-live-" + suffix));
        if (!directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        Directory.CreateDirectory(directory);
        try
        {
            var workerSecrets = Path.Combine(directory, "worker"); var gatewaySecrets = Path.Combine(directory, "gateway");
            Directory.CreateDirectory(workerSecrets); Directory.CreateDirectory(gatewaySecrets);
            WriteCertificate(workerSecrets, "server", workerServerCert); WriteCertificate(workerSecrets, "authority-client", workerCert);
            File.WriteAllText(Path.Combine(workerSecrets, "authority-ca.crt"), authorityCert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(workerSecrets, "gateway-client-ca.crt"), gatewayCert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(workerSecrets, "worker-token"), "fixture-worker-token\n");
            WriteCertificate(gatewaySecrets, "gateway", gatewayCert);
            File.WriteAllText(Path.Combine(gatewaySecrets, "authority-ca.crt"), authorityCert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(gatewaySecrets, "worker-ca.crt"), workerServerCert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(gatewaySecrets, "worker-token"), "fixture-worker-token\n");
            File.WriteAllText(Path.Combine(gatewaySecrets, "fingerprints"), "key:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
            await Docker("network", "create", network);
            var authority = $"https://host.docker.internal:{authorityPort}";
            var modelEnv = new[] { "OPENCONNECT_CLOUD_MODEL_PATH=/models/liveui.gguf", "OPENCONNECT_CLOUD_MODEL_ID=" + modelId,
                "OPENCONNECT_CLOUD_MODEL_REVISION=" + revision, "OPENCONNECT_CLOUD_MODEL_DIGEST=" + digest, "OPENCONNECT_CLOUD_MODEL_FORMAT=chatml",
                "OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED=true", "OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED=true", "OPENCONNECT_CLOUD_MODEL_CONTEXT_TOKENS=512" };
            var workerArgs = new List<string> { "run", "-d", "--name", worker, "--network", network, "--network-alias", "cloud-inference-worker",
                "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "-v", workerSecrets + ":/run/openconnect-cloud:ro",
                "-v", Required("OPENCONNECT_TEST_MODEL_VOLUME") + ":/models:ro" };
            foreach (var value in modelEnv.Concat(new[] { "OPENCONNECT_WORKER_TOKEN_FILE=/run/openconnect-cloud/worker-token",
                "OPENCONNECT_CLOUD_AUTHORITY_ORIGIN=" + authority, "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE=/run/openconnect-cloud/authority-ca.crt",
                "OPENCONNECT_CLOUD_WORKER_CERT_FILE=/run/openconnect-cloud/authority-client.crt", "OPENCONNECT_CLOUD_WORKER_KEY_FILE=/run/openconnect-cloud/authority-client.key" }))
                workerArgs.AddRange(["-e", value]);
            workerArgs.Add(Required("OPENCONNECT_TEST_WORKER_IMAGE")); await Docker(workerArgs.ToArray());
            var gatewayArgs = new List<string> { "run", "-d", "--name", gateway, "--network", network, "-p", "127.0.0.1::8091",
                "-v", gatewaySecrets + ":/run/openconnect-cloud:ro", "-v", Required("OPENCONNECT_TEST_MODEL_VOLUME") + ":/models:ro" };
            var gatewayEnv = new[] { "CLOUD_AUTHORITY_ORIGIN=" + authority, "CLOUD_AUTHORITY_CA_FILE=/run/openconnect-cloud/authority-ca.crt",
                "CLOUD_AUTHORITY_CERT_FILE=/run/openconnect-cloud/gateway.crt", "CLOUD_AUTHORITY_KEY_FILE=/run/openconnect-cloud/gateway.key",
                "CLOUD_INFERENCE_TOKENIZER_ENABLED=true", "CLOUD_INFERENCE_EXECUTION_ENABLED=true", "CLOUD_INFERENCE_ORIGIN=https://cloud-inference-worker:8080",
                "CLOUD_INFERENCE_CA_FILE=/run/openconnect-cloud/worker-ca.crt", "CLOUD_INFERENCE_CERT_FILE=/run/openconnect-cloud/gateway.crt",
                "CLOUD_INFERENCE_KEY_FILE=/run/openconnect-cloud/gateway.key", "CLOUD_INFERENCE_TOKEN_FILE=/run/openconnect-cloud/worker-token",
                "CLOUD_INFERENCE_DEPLOYMENT_ID=" + deploymentId, "CLOUD_INFERENCE_ENDPOINT_REFERENCE=" + deploymentRef,
                "CLOUD_INFERENCE_REGION=region-a", "CLOUD_INFERENCE_CONTRACT_VERSION=openconnect.v1", "CLOUD_FINGERPRINT_ACTIVE_KEY_ID=key",
                "CLOUD_FINGERPRINT_KEYS_FILE=/run/openconnect-cloud/fingerprints" };
            foreach (var value in gatewayEnv.Concat(modelEnv.Select(x => x.Replace("OPENCONNECT_CLOUD_MODEL_", "CLOUD_INFERENCE_MODEL_", StringComparison.Ordinal))))
                gatewayArgs.AddRange(["-e", value]);
            gatewayArgs.Add(Required("OPENCONNECT_TEST_GATEWAY_IMAGE")); await Docker(gatewayArgs.ToArray());
            var portText = await Docker("port", gateway, "8091/tcp");
            var port = int.Parse(portText.Trim().Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(90) };
            var ready = false;
            for (var attempt = 0; attempt < 30 && !ready; attempt++)
            {
                using var check = await http.GetAsync("/health/ready");
                ready = check.IsSuccessStatusCode;
                if (!ready) await Task.Delay(1000);
            }
            Assert.True(ready, "Cloud gateway and worker did not become ready.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
            var requestId = Guid.NewGuid();
            var request = new { schemaVersion = "openconnect.cloud.inference/v1", requestId, capability = "inference.chat",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture), modelId,
                input = new { messages = new[] { new { role = "user", content = "Say hello." } }, maxTokens = 8, stream = false } };
            using var response = await http.PostAsJsonAsync("/cloud/v1/inference/chat", request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var result = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(requestId.ToString(), result.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.String, result.RootElement.GetProperty("output").ValueKind);
            var usage = result.RootElement.GetProperty("usage");
            var inputTokens = usage.GetProperty("inputTokens").GetInt64(); var outputTokens = usage.GetProperty("outputTokens").GetInt64();
            Assert.True(inputTokens > 0); Assert.InRange(outputTokens, 0, 8);
            await using var verify = await factory.CreateDbContextAsync();
            var reservation = await verify.QuotaReservations.SingleAsync(x => x.RequestId == requestId && x.TenantId == tenant.Id && x.ApplicationId == application.Id);
            Assert.NotNull(reservation.FinalizedAt); Assert.Equal(inputTokens + outputTokens, reservation.Actual);
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.inference_privacy_evidence WHERE \"TenantId\" = {tenant.Id} AND \"ApplicationId\" = {application.Id} AND \"RequestId\" = {requestId} AND \"Outcome\" = 'allowed'").SingleAsync());
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.inference_output_privacy_evidence WHERE \"ReservationId\" = {reservation.Id} AND \"Outcome\" = 'allowed'").SingleAsync());
            using var replay = await http.PostAsJsonAsync("/cloud/v1/inference/chat", request);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal(1, await verify.ExecutionConsumptions.CountAsync(x => x.ReservationId == reservation.Id));
        }
        finally
        {
            await DockerIgnoreFailure("rm", "-f", gateway); await DockerIgnoreFailure("rm", "-f", worker);
            await DockerIgnoreFailure("network", "rm", network); Directory.Delete(directory, recursive: true);
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Missing test configuration.");
    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker unavailable.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Docker command failed: " + await errors);
        return await output;
    }
    private static async Task DockerIgnoreFailure(params string[] args) { try { await Docker(args); } catch { } }
    private static void WriteCertificate(string directory, string name, X509Certificate2 certificate)
    {
        File.WriteAllText(Path.Combine(directory, name + ".crt"), certificate.ExportCertificatePem());
        using var key = certificate.GetRSAPrivateKey()!; File.WriteAllText(Path.Combine(directory, name + ".key"), key.ExportPkcs8PrivateKeyPem());
    }
    private static X509Certificate2 Certificate(string name, bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client) { var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(name); request.CertificateExtensions.Add(names.Build()); }
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
