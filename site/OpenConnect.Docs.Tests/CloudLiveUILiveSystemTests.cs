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
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Docs.Cloud;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudLiveUILiveFactAttribute : FactAttribute
{
    public CloudLiveUILiveFactAttribute()
    {
        if (new[] { "OPENCONNECT_CLOUD_TEST_DATABASE", "OPENCONNECT_TEST_GATEWAY_IMAGE", "OPENCONNECT_TEST_WORKER_IMAGE",
            "OPENCONNECT_TEST_MODEL_VOLUME", "OPENCONNECT_TEST_MODEL_REVISION", "OPENCONNECT_TEST_MODEL_DIGEST" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Requires a disposable Cloud database, test images and an already-provisioned approved model volume.";
    }
}

[Collection("Cloud PostgreSQL")]
public sealed class CloudLiveUILiveSystemTests
{
    [CloudLiveUILiveFact]
    public async Task GatewayExecutesLiveUIAndRecordsSettlementAndPrivacy()
    {
        const string modelId = "bartowski/Qwen_Qwen3-0.6B-GGUF";
        var revision = Required("OPENCONNECT_TEST_MODEL_REVISION");
        var digest = Required("OPENCONNECT_TEST_MODEL_DIGEST");
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Required("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Cloud LiveUI acceptance", default);
        var application = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Fixture app", "Development", default)).Application!;
        var deploymentId = Guid.NewGuid();
        var deploymentRef = "liveui-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.liveui");
            capability.PlatformAvailability = "available"; capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "inference.liveui", EndpointReference = deploymentRef,
                Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = modelId,
                ModelRevision = revision, ModelDigest = digest, ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "inference.liveui", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "inference.liveui", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = application.Id, CapabilityId = "inference.liveui", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = application.Id,
                CapabilityId = "inference.liveui", Unit = "plan", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(15), Limit = 10 });
            await db.SaveChangesAsync();
        }
        var policy = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
        Assert.Equal("succeeded", (await policy.PutAsync(owner, tenant.Id, [modelId], 0, default)).Status);
        var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
        var credential = await credentials.CreateAsync(owner, tenant.Id, application.Id, ["inference.liveui"], now.AddMinutes(10), default);
        Assert.Equal("succeeded", credential.Status);

        using var authorityCert = Certificate("host.docker.internal", false);
        using var workerServerCert = Certificate("cloud-liveui-worker", false);
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
        builder.Services.AddScoped<ICloudLiveUIClaimStore, EfCloudLiveUIClaimStore>(); builder.Services.AddScoped<CloudLiveUIClaimService>();
        builder.Services.AddScoped<ICloudLiveUIPrivacyEvidenceStore, EfCloudLiveUIPrivacyEvidenceStore>();
        builder.Services.AddSingleton(new CloudLiveUIPrivacyService(new RulesSecurityDetector()));
        builder.Services.AddScoped<ICloudLiveUIAccessStore, EfCloudLiveUIAccessStore>();
        builder.Services.AddScoped<ICloudLiveUIOutputEvidenceStore, EfCloudLiveUIOutputEvidenceStore>();
        builder.Services.AddSingleton(new CloudLiveUIOutputPrivacyService(new RulesSecurityDetector()));
        builder.Services.AddScoped<ICloudLiveUIConsumptionStore, EfCloudLiveUIConsumptionStore>(); builder.Services.AddScoped<CloudLiveUIConsumptionService>();
        builder.Services.AddScoped<ICloudLiveUISettlementStore, EfCloudLiveUISettlementStore>(); builder.Services.AddScoped<CloudLiveUISettlement>();
        builder.Services.AddScoped<ICloudWorkerReadinessStore, EfCloudWorkerReadinessStore>(); builder.Services.AddScoped<CloudWorkerReadiness>();
        builder.Services.AddScoped<CloudReadinessService>();
        await using var host = builder.Build();
        host.MapCloudGatewayReadiness(gatewayTrust, true); host.MapCloudGatewayAuthorization(gatewayTrust, true);
        host.MapCloudLiveUIPrivacy(gatewayTrust, true); host.MapCloudLiveUIQuota(gatewayTrust, true); host.MapCloudLiveUIClaim(gatewayTrust, true);
        host.MapCloudLiveUIAccess(gatewayTrust, true); host.MapCloudLiveUIOutputPrivacy(gatewayTrust, true);
        host.MapCloudWorkerReadiness(workerTrust, true); host.MapCloudLiveUIConsumption(workerTrust, true); host.MapCloudLiveUISettlement(workerTrust, true);
        await host.StartAsync();
        var authorityPort = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var network = "oc-liveui-live-" + suffix; var worker = "oc-liveui-worker-" + suffix; var gateway = "oc-liveui-gateway-" + suffix;
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.GetFullPath(Path.Combine(temporaryRoot, "oc-liveui-live-" + suffix));
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
            var modelEnv = new[] { "OPENCONNECT_CLOUD_LIVEUI_ENABLED=true", "MODEL_ENABLED=true", "MODEL_PATH=/models/liveui.gguf",
                "MODEL_ID=" + modelId, "MODEL_REVISION=" + revision, "MODEL_SHA256=" + digest["sha256:".Length..],
                "OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED=true", "OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED=true" };
            var workerArgs = new List<string> { "run", "-d", "--name", worker, "--network", network, "--network-alias", "cloud-liveui-worker",
                "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "-v", workerSecrets + ":/run/openconnect-cloud:ro",
                "-v", Required("OPENCONNECT_TEST_MODEL_VOLUME") + ":/models:ro" };
            foreach (var value in modelEnv.Concat(new[] { "OPENCONNECT_WORKER_TOKEN_FILE=/run/openconnect-cloud/worker-token",
                "OPENCONNECT_CLOUD_AUTHORITY_ORIGIN=" + authority, "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE=/run/openconnect-cloud/authority-ca.crt",
                "OPENCONNECT_CLOUD_WORKER_CERT_FILE=/run/openconnect-cloud/authority-client.crt", "OPENCONNECT_CLOUD_WORKER_KEY_FILE=/run/openconnect-cloud/authority-client.key" }))
                workerArgs.AddRange(["-e", value]);
            workerArgs.Add(Required("OPENCONNECT_TEST_WORKER_IMAGE"));
            workerArgs.AddRange(["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080",
                "--ssl-certfile", "/run/openconnect-cloud/server.crt", "--ssl-keyfile", "/run/openconnect-cloud/server.key",
                "--ssl-ca-certs", "/run/openconnect-cloud/gateway-client-ca.crt", "--ssl-cert-reqs", "2", "--no-access-log"]);
            await Docker(workerArgs.ToArray());
            var gatewayArgs = new List<string> { "run", "-d", "--name", gateway, "--network", network, "-p", "127.0.0.1::8091",
                "-v", gatewaySecrets + ":/run/openconnect-cloud:ro", "-v", Required("OPENCONNECT_TEST_MODEL_VOLUME") + ":/models:ro" };
            var gatewayEnv = new[] { "CLOUD_AUTHORITY_ORIGIN=" + authority, "CLOUD_AUTHORITY_CA_FILE=/run/openconnect-cloud/authority-ca.crt",
                "CLOUD_AUTHORITY_CERT_FILE=/run/openconnect-cloud/gateway.crt", "CLOUD_AUTHORITY_KEY_FILE=/run/openconnect-cloud/gateway.key",
                "CLOUD_LIVEUI_EXECUTION_ENABLED=true", "CLOUD_LIVEUI_ORIGIN=https://cloud-liveui-worker:8080",
                "CLOUD_LIVEUI_CA_FILE=/run/openconnect-cloud/worker-ca.crt", "CLOUD_LIVEUI_CERT_FILE=/run/openconnect-cloud/gateway.crt",
                "CLOUD_LIVEUI_KEY_FILE=/run/openconnect-cloud/gateway.key", "CLOUD_LIVEUI_TOKEN_FILE=/run/openconnect-cloud/worker-token",
                "CLOUD_LIVEUI_DEPLOYMENT_ID=" + deploymentId, "CLOUD_LIVEUI_ENDPOINT_REFERENCE=" + deploymentRef,
                "CLOUD_LIVEUI_REGION=region-a", "CLOUD_LIVEUI_CONTRACT_VERSION=openconnect.v1", "CLOUD_LIVEUI_MODEL_ID=" + modelId,
                "CLOUD_LIVEUI_MODEL_REVISION=" + revision, "CLOUD_LIVEUI_MODEL_DIGEST=" + digest,
                "CLOUD_FINGERPRINT_ACTIVE_KEY_ID=key", "CLOUD_FINGERPRINT_KEYS_FILE=/run/openconnect-cloud/fingerprints" };
            foreach (var value in gatewayEnv) gatewayArgs.AddRange(["-e", value]);
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
            var request = new { schemaVersion = "openconnect.cloud.liveui/v1", requestId, capability = "inference.liveui",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                input = new { schemaVersion = "v1", intent = "explain current view", context = new { route = "/tasks",
                    availableActions = new[] { new { target = "region-tasks", action = "inspect" } } },
                    generation = new { maxActions = 2, maxOutputTokens = 32 } } };
            using var response = await http.PostAsJsonAsync("/cloud/v1/liveui/plan", request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var result = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(requestId.ToString(), result.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("deterministic", result.RootElement.GetProperty("provider").GetString());
            Assert.Equal("region-tasks", result.RootElement.GetProperty("plan").GetProperty("actions")[0].GetProperty("target").GetString());
            await using var verify = await factory.CreateDbContextAsync();
            var reservation = await verify.QuotaReservations.SingleAsync(x => x.RequestId == requestId && x.TenantId == tenant.Id && x.ApplicationId == application.Id);
            Assert.NotNull(reservation.FinalizedAt); Assert.Equal(1, reservation.Actual);
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_privacy_evidence WHERE \"TenantId\" = {tenant.Id} AND \"ApplicationId\" = {application.Id} AND \"RequestId\" = {requestId} AND \"Outcome\" = 'allowed'").SingleAsync());
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_output_privacy_evidence WHERE \"ReservationId\" = {reservation.Id} AND \"Outcome\" = 'allowed'").SingleAsync());
            using var replay = await http.PostAsJsonAsync("/cloud/v1/liveui/plan", request);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal(1, await verify.ExecutionConsumptions.CountAsync(x => x.ReservationId == reservation.Id));
            await VerifyHostPlanAsync(port, credential.Secret!, factory, tenant.Id, application.Id);
            var modelRequestId = Guid.NewGuid();
            var modelRequest = new { schemaVersion = "openconnect.cloud.liveui/v1", requestId = modelRequestId,
                capability = "inference.liveui", expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                input = new { schemaVersion = "openconnect.liveui/v1", intent = "Select the Completed tab.", context = new { route = "todo.home",
                    availableActions = new[] { new { target = "todo.tab.all", action = "activate" }, new { target = "todo.tab.active", action = "activate" },
                        new { target = "todo.tab.completed", action = "activate" } } }, generation = new { maxActions = 8, maxOutputTokens = 128 } } };
            using var modelResponse = await http.PostAsJsonAsync("/cloud/v1/liveui/plan", modelRequest);
            Assert.True(modelResponse.IsSuccessStatusCode, await modelResponse.Content.ReadAsStringAsync());
            using var modelResult = System.Text.Json.JsonDocument.Parse(await modelResponse.Content.ReadAsStringAsync());
            Assert.Equal("qwen3-0.6b", modelResult.RootElement.GetProperty("provider").GetString());
            var modelAction = Assert.Single(modelResult.RootElement.GetProperty("plan").GetProperty("actions").EnumerateArray());
            Assert.Equal("activate", modelAction.GetProperty("type").GetString());
            Assert.Equal("todo.tab.completed", modelAction.GetProperty("target").GetString());
            var modelReservation = await verify.QuotaReservations.SingleAsync(item => item.TenantId == tenant.Id && item.RequestId == modelRequestId);
            Assert.Equal(1, modelReservation.Actual); Assert.NotNull(modelReservation.FinalizedAt);
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_privacy_evidence WHERE \"TenantId\" = {tenant.Id} AND \"RequestId\" = {modelRequestId} AND \"Outcome\" = 'allowed'").SingleAsync());
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_output_privacy_evidence WHERE \"ReservationId\" = {modelReservation.Id} AND \"Outcome\" = 'allowed'").SingleAsync());
        }
        finally
        {
            await DockerIgnoreFailure("rm", "-f", gateway); await DockerIgnoreFailure("rm", "-f", worker);
            await DockerIgnoreFailure("network", "rm", network); Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyHostPlanAsync(int gatewayPort, string credential,
        IDbContextFactory<CloudControlPlaneDbContext> factory, Guid tenantId, Guid applicationId)
    {
        // The production transport validates TLS normally; this isolated proxy pins its own ephemeral test certificate.
        using var certificate = Certificate("localhost", false);
        var proxyBuilder = WebApplication.CreateBuilder();
        proxyBuilder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        await using var proxy = proxyBuilder.Build();
        using var gateway = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gatewayPort}") };
        proxy.Run(async context =>
        {
            using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
                outgoing.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
            if (context.Request.Method == "POST")
            {
                outgoing.Content = new StreamContent(context.Request.Body);
                outgoing.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await gateway.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await proxy.StartAsync();
        var proxyPort = new Uri(proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, received, _, _) => received is not null &&
                received.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256) };
        using var transport = (CloudLiveUIHttpTransport)Activator.CreateInstance(typeof(CloudLiveUIHttpTransport),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, binder: null,
            args: [new Uri($"https://localhost:{proxyPort}/"), credential, handler], culture: null)!;

        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        var page = new LiveUIAgentPage("/tasks", "/tasks", "Tasks", "build", "hash",
            [new("region-tasks", "Region", "Tasks", new HashSet<LiveUICapability> { LiveUICapability.Inspect }, false)]);
        var authorizer = new CloudLiveUIHostAuthorizer(new TestCurrentContext(context.Session.Value.ToString()),
            new TestManifest(), new TestPage(page), new TestSession(context));
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));
        var provider = new CloudLiveUIInferenceProvider(authorizer, new EgressGate(firewall), transport,
            Options.Create(new LiveUIInferenceOptions()));
        Assert.Equal(LiveUIInferenceAvailabilityStatus.Available, (await provider.GetAvailabilityAsync()).Status);
        var execution = Guid.NewGuid();
        var request = new LiveUIInferenceRequest("explain current view", context.Session.Value.ToString(), "/tasks", "v1",
            LiveUIInferenceCapabilities.FromPage(page), "hash") { ExecutionId = execution.ToString("N") };
        var result = await provider.GeneratePlanAsync(request);
        Assert.Equal("completed", result.Outcome);
        Assert.Equal("region-tasks", Assert.Single(result.Plan!.Actions).TargetId);
        await using var verify = await factory.CreateDbContextAsync();
        var reservation = await verify.QuotaReservations.SingleAsync(item => item.TenantId == tenantId && item.ApplicationId == applicationId && item.RequestId == execution);
        Assert.Equal(1, reservation.Actual); Assert.NotNull(reservation.FinalizedAt);
        Assert.Equal(1, await verify.ExecutionConsumptions.CountAsync(item => item.ReservationId == reservation.Id));
        await proxy.StopAsync();
    }

    private sealed class TestCurrentContext(string session) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(new(session, "/tasks") { CurrentPath = "/tasks" });
    }
    private sealed class TestManifest : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(new("v1", "build", "hash", []));
    }
    private sealed class TestPage(LiveUIAgentPage page) : ICloudLiveUIPageProvider
    {
        public ValueTask<LiveUIAgentPage?> GetCurrentPageAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<LiveUIAgentPage?>(page);
    }
    private sealed class TestSession(EgressContext context) : ICloudLiveUISessionContextProvider
    {
        public ValueTask<EgressContext?> GetContextAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<EgressContext?>(context);
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

