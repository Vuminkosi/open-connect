using System.Net;
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

[Collection("Cloud PostgreSQL")]
public sealed class CloudGatewayAuthorizationPostgresTests
{
    [CloudPostgresFact]
    public async Task DirectTlsAndPinnedClientCertificateProtectCredentialAuthorization()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        string? originalAvailability = null;
        string? originalClassificationAvailability = null;
        try
        {
            var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
            var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
            var tenant = await tenants.CreateTenantAsync(owner, "Gateway test", CancellationToken.None);
            var appRecord = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
            var capability = "inference.chat";
            await using (var db = await factory.CreateDbContextAsync())
            {
                var definition = await db.Capabilities.SingleAsync(x => x.Id == capability);
                var classification = await db.Capabilities.SingleAsync(x => x.Id == "classification.classify");
                originalAvailability = definition.PlatformAvailability;
                originalClassificationAvailability = classification.PlatformAvailability;
                definition.PlatformAvailability = "available";
                classification.PlatformAvailability = "available";
                db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capability, PlanGranted = true, Revision = 1 });
                db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capability, Enabled = true, Revision = 1 });
                db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = appRecord.Id, CapabilityId = capability, Enabled = true, Revision = 1 });
                db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "classification.classify", PlanGranted = true, Revision = 1 });
                db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
                db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = appRecord.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
                await db.SaveChangesAsync();
            }
            var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
            var issued = await credentials.CreateAsync(owner, tenant.Id, appRecord.Id, [capability], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
            Assert.Equal("succeeded", issued.Status);
            var classificationCredential = await credentials.CreateAsync(owner, tenant.Id, appRecord.Id, ["classification.classify"], DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
            Assert.Equal("succeeded", classificationCredential.Status);
            using var serverCertificate = Certificate(client: false);
            using var clientCertificate = Certificate(client: true);
            using var otherCertificate = Certificate(client: true);
            Assert.Throws<InvalidOperationException>(() => new CloudGatewayClientTrust([]));
            Assert.Throws<InvalidOperationException>(() => new CloudGatewayClientTrust(["invalid"]));
            var trust = new CloudGatewayClientTrust([Convert.ToHexString(clientCertificate.GetCertHash(HashAlgorithmName.SHA256))]);
            Assert.True(trust.IsTrusted(clientCertificate, DateTimeOffset.UtcNow));
            Assert.False(trust.IsTrusted(otherCertificate, DateTimeOffset.UtcNow));
            Assert.False(trust.IsTrusted(serverCertificate, DateTimeOffset.UtcNow));
            Assert.False(trust.IsTrusted(clientCertificate, DateTimeOffset.UtcNow.AddDays(2)));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(serverCertificate, https =>
            {
                trust.ConfigureHttps(https);
            })));
            builder.Services.AddSingleton(factory);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>();
            builder.Services.AddScoped<CloudCredentialService>();
            builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>();
            builder.Services.AddScoped<CloudMachineAuthorizationService>();
            builder.Services.AddSingleton(new CloudClassificationPrivacyService(new RulesSecurityDetector()));
            var evidenceStore = new SwitchableEvidenceStore(new EfCloudPrivacyEvidenceStore(factory, TimeProvider.System));
            builder.Services.AddSingleton<ICloudPrivacyEvidenceStore>(evidenceStore);
            await using var app = builder.Build();
            app.MapCloudGatewayAuthorization(trust, databaseConfigured: true);
            app.MapCloudClassificationPrivacy(trust, databaseConfigured: true);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var address = new Uri(addresses.Addresses.Single());
            using var anonymous = Client(address, serverCertificate, null);
            anonymous.DefaultRequestHeaders.Add("X-Client-Cert", Convert.ToBase64String(clientCertificate.RawData));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability })).StatusCode);
            using var other = Client(address, serverCertificate, otherCertificate);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => other.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability }));
            using var authorized = Client(address, serverCertificate, clientCertificate);
            var response = await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var result = await response.Content.ReadFromJsonAsync<CloudGatewayAuthorizationResponse>();
            Assert.True(result!.Authorized);
            Assert.Equal(tenant.Id, result.Identity!.TenantId);
            Assert.Equal(appRecord.Id, result.Identity.ApplicationId);
            Assert.True((await NodeAuthorizeAsync(address, serverCertificate, clientCertificate, issued.Secret!, capability)).Authorized);
            var gatewayResult = await NodeGatewayAsync(address, serverCertificate, clientCertificate, issued.Secret!);
            Assert.True(gatewayResult.Checked);
            Assert.Equal(503, gatewayResult.Status);
            Assert.Equal("capability_unavailable", gatewayResult.Code);
            Assert.Equal("allowed", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world")).Code);
            Assert.Equal("privacy_blocked", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "email person@example.com")).Code);
            Assert.Equal("capability_unavailable", (await NodePrivacyAsync(address, serverCertificate, otherCertificate, classificationCredential.Secret!, "hello world")).Code);
            using (var privacyRequest = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/classification/privacy"))
            {
                privacyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", classificationCredential.Secret);
                privacyRequest.Headers.Add("X-Cloud-Request-Id", Guid.NewGuid().ToString("D"));
                privacyRequest.Content = new StringContent(new string('x', 65537), System.Text.Encoding.UTF8, "text/plain");
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await authorized.SendAsync(privacyRequest)).StatusCode);
            }
            var blocked = await NodeClassificationGatewayAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "email person@example.com");
            Assert.Equal(403, blocked.Status);
            Assert.Equal("privacy_blocked", blocked.Code);
            var clean = await NodeClassificationGatewayAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world");
            Assert.Equal(503, clean.Status);
            Assert.Equal("capability_unavailable", clean.Code);
            await using (var verify = await factory.CreateDbContextAsync())
            {
                var evidence = await verify.PrivacyEvidence.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ApplicationId == appRecord.Id).ToListAsync();
                Assert.Equal(2, evidence.Count(x => x.Outcome == "allowed"));
                Assert.Equal(2, evidence.Count(x => x.Outcome == "privacy_blocked"));
                Assert.All(evidence, x => { Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), x.RequestId); Assert.Matches("^[0-9a-f]{32}$", x.TraceId); });
                Assert.DoesNotContain("person@example.com", System.Text.Json.JsonSerializer.Serialize(evidence));
                await Assert.ThrowsAnyAsync<Exception>(() => verify.PrivacyEvidence.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => verify.PrivacyEvidence.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Outcome, "allowed")));
                await Assert.ThrowsAnyAsync<Exception>(() => verify.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.privacy_evidence"));
            }
            evidenceStore.Fail = true;
            Assert.Equal("capability_unavailable", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world")).Code);
            evidenceStore.Fail = false;
            var foreign = new CloudCredentialIdentity(Guid.NewGuid(), Guid.NewGuid(), appRecord.Id, "Development", new HashSet<string>());
            await Assert.ThrowsAsync<DbUpdateException>(() => evidenceStore.AppendAsync(foreign, Guid.NewGuid(), "allowed", Guid.NewGuid().ToString("N"), CancellationToken.None));
            Assert.False((await NodeAuthorizeAsync(address, serverCertificate, otherCertificate, issued.Secret!, capability)).Authorized);
            Assert.False((await NodeAuthorizeAsync(address, otherCertificate, clientCertificate, issued.Secret!, capability)).Authorized);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = new string('x', 5000), capability })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability, tenantId = Guid.NewGuid() })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability = "other" })).StatusCode);
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, appRecord.Id, issued.Credential!.Id, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Unauthorized, (await authorized.PostAsJsonAsync("/cloud/internal/v1/authorize", new { credential = issued.Secret, capability })).StatusCode);
            Assert.Equal("unauthorized", (await NodeAuthorizeAsync(address, serverCertificate, clientCertificate, issued.Secret!, capability)).Code);
            var revokedGatewayResult = await NodeGatewayAsync(address, serverCertificate, clientCertificate, issued.Secret!);
            Assert.False(revokedGatewayResult.Checked);
            Assert.Equal(401, revokedGatewayResult.Status);
            Assert.Equal("unauthorized", revokedGatewayResult.Code);
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, appRecord.Id, classificationCredential.Credential!.Id, CancellationToken.None));
            Assert.Equal("capability_unavailable", (await NodePrivacyAsync(address, serverCertificate, clientCertificate, classificationCredential.Secret!, "hello world")).Code);
        }
        finally
        {
            if (originalAvailability is not null)
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Capabilities.Where(x => x.Id == "inference.chat")
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, originalAvailability));
            }
            if (originalClassificationAvailability is not null)
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Capabilities.Where(x => x.Id == "classification.classify")
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, originalClassificationAvailability));
            }
        }
    }

    private sealed class SwitchableEvidenceStore(ICloudPrivacyEvidenceStore inner) : ICloudPrivacyEvidenceStore
    {
        public bool Fail { get; set; }
        public Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string outcome, string traceId, CancellationToken cancellationToken) =>
            Fail ? Task.FromException(new InvalidOperationException("test evidence unavailable")) : inner.AppendAsync(identity, requestId, outcome, traceId, cancellationToken);
    }

    private static async Task<CloudPrivacyDecision> NodePrivacyAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string content)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); process.stdout.write(JSON.stringify(await c.inspectClassification(process.env.OC_TEST_CREDENTIAL,'11111111-1111-1111-1111-111111111111',process.env.OC_TEST_CAPABILITY)));";
        var output = await NodeCallAsync(script, address, server, client, credential, content);
        return System.Text.Json.JsonSerializer.Deserialize<CloudPrivacyDecision>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<GatewayResult> NodeClassificationGatewayAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string content)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const g=await import(process.env.OC_TEST_GATEWAY); const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); const s=g.createLocalServer(c,{probe:async()=>({ready:true})}); await new Promise(r=>s.listen(0,'127.0.0.1',r)); const x=await fetch('http://127.0.0.1:'+s.address().port+'/cloud/v1/classification',{method:'POST',headers:{authorization:'Bearer '+process.env.OC_TEST_CREDENTIAL,'content-type':'application/json'},body:JSON.stringify({schemaVersion:'openconnect.cloud.classification/v1',requestId:'11111111-1111-1111-1111-111111111111',capability:'classification.classify',input:{text:process.env.OC_TEST_CAPABILITY}})}); const b=await x.json(); await new Promise(r=>s.close(r)); process.stdout.write(JSON.stringify({status:x.status,checked:true,code:b.code}));";
        var output = await NodeCallAsync(script, address, server, client, credential, content);
        return System.Text.Json.JsonSerializer.Deserialize<GatewayResult>(output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<CloudGatewayAuthorizationResponse> NodeAuthorizeAsync(
        Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string capability)
    {
        var script = "const m = await import(process.env.OC_TEST_MODULE); " +
            "const c = m.createCloudAuthorizationClient({origin: process.env.OC_TEST_ORIGIN, ca: process.env.OC_TEST_CA, certificate: process.env.OC_TEST_CERT, privateKey: process.env.OC_TEST_KEY}); " +
            "process.stdout.write(JSON.stringify(await c.authorize(process.env.OC_TEST_CREDENTIAL, process.env.OC_TEST_CAPABILITY)));";
        var output = await NodeCallAsync(script, address, server, client, credential, capability);
        return System.Text.Json.JsonSerializer.Deserialize<CloudGatewayAuthorizationResponse>(output,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private sealed record GatewayResult(int Status, bool Checked, string Code);

    private static async Task<GatewayResult> NodeGatewayAsync(Uri address, X509Certificate2 server, X509Certificate2 client, string credential)
    {
        var script = "const m=await import(process.env.OC_TEST_MODULE); const g=await import(process.env.OC_TEST_GATEWAY); " +
            "const c=m.createCloudAuthorizationClient({origin:process.env.OC_TEST_ORIGIN,ca:process.env.OC_TEST_CA,certificate:process.env.OC_TEST_CERT,privateKey:process.env.OC_TEST_KEY}); " +
            "let checked=false; const s=g.createLocalServer({authorize:async (...a)=>{const r=await c.authorize(...a); checked=r.authorized; return r;}}); " +
            "await new Promise(r=>s.listen(0,'127.0.0.1',r)); const x=await fetch('http://127.0.0.1:'+s.address().port+'/cloud/v1/inference/chat',{method:'POST',headers:{authorization:'Bearer '+process.env.OC_TEST_CREDENTIAL},body:'private-test-body'}); " +
            "const b=await x.json(); await new Promise(r=>s.close(r)); process.stdout.write(JSON.stringify({status:x.status,checked,code:b.code}));";
        var output = await NodeCallAsync(script, address, server, client, credential, "inference.chat");
        return System.Text.Json.JsonSerializer.Deserialize<GatewayResult>(output,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }

    private static async Task<string> NodeCallAsync(string script, Uri address, X509Certificate2 server, X509Certificate2 client, string credential, string capability)
    {
        var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        start.Environment["OC_TEST_MODULE"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "control-plane.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_GATEWAY"] = new Uri(Path.Combine(AppContext.BaseDirectory, "gateway", "local-server.mjs")).AbsoluteUri;
        start.Environment["OC_TEST_ORIGIN"] = address.ToString();
        start.Environment["OC_TEST_CA"] = server.ExportCertificatePem();
        start.Environment["OC_TEST_CERT"] = client.ExportCertificatePem();
        using var privateKey = client.GetRSAPrivateKey()!;
        start.Environment["OC_TEST_KEY"] = privateKey.ExportPkcs8PrivateKeyPem();
        start.Environment["OC_TEST_CREDENTIAL"] = credential;
        start.Environment["OC_TEST_CAPABILITY"] = capability;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
        return output;
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

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
