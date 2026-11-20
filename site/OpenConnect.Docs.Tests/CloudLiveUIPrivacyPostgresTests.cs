using System.Net;
using System.Net.Http.Headers;
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
public sealed class CloudLiveUIPrivacyPostgresTests
{
    [CloudPostgresFact]
    public async Task GatewayIdentityAndCredentialAreRequiredAndEvidenceContainsNoRawFields()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "LiveUI privacy", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Fixture", "Development", default)).Application!;
        string previousAvailability;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.liveui");
            previousAvailability = capability.PlatformAvailability; capability.PlatformAvailability = "available";
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "inference.liveui", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "inference.liveui", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.liveui", Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        try
        {
            var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
            var issued = await credentials.CreateAsync(owner, tenant.Id, app.Id, ["inference.liveui"], DateTimeOffset.UtcNow.AddHours(1), default);
            Assert.Equal("succeeded", issued.Status);
            using var server = Certificate(false); using var gateway = Certificate(true); using var other = Certificate(true);
            var trust = new CloudGatewayClientTrust([Convert.ToHexString(gateway.GetCertHash(HashAlgorithmName.SHA256))]);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(server, https =>
            {
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            })));
            builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>(); builder.Services.AddScoped<CloudCredentialService>();
            builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>(); builder.Services.AddScoped<CloudMachineAuthorizationService>();
            builder.Services.AddSingleton(new CloudLiveUIPrivacyService(new RulesSecurityDetector()));
            builder.Services.AddScoped<ICloudLiveUIPrivacyEvidenceStore, EfCloudLiveUIPrivacyEvidenceStore>();
            await using var host = builder.Build(); host.MapCloudLiveUIPrivacy(trust, true); await host.StartAsync();
            var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = Client(address, server, gateway); using var untrusted = Client(address, server, other);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
            untrusted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
            var requestId = Guid.NewGuid();
            async Task<HttpResponseMessage> Inspect(HttpClient sender, string body)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/cloud/internal/v1/liveui/privacy")
                    { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                message.Headers.Add("X-Cloud-Request-Id", requestId.ToString("D"));
                message.Headers.Add("X-Cloud-Fingerprint-Key-Id", "key");
                message.Headers.Add("X-Cloud-Request-Fingerprint", Convert.ToBase64String(new byte[32]));
                return await sender.SendAsync(message);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Inspect(untrusted, "{\"fields\":[\"v1\",\"safe\",\"route\"]}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Inspect(client, "{\"fields\":[],\"Fields\":[]}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Inspect(client, "{\"fields\":[\"v1\",\"show tasks\",\"route.root\"]}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Inspect(client, "{\"fields\":[\"v1\",\"person@\",\"example.test\"]}")).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_privacy_evidence WHERE \"RequestId\" = {requestId} AND \"TenantId\" = {tenant.Id} AND \"ApplicationId\" = {app.Id}").SingleAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.liveui_privacy_evidence WHERE \"RequestId\" = {requestId}"));
            }
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, app.Id, issued.Credential!.Id, default));
            Assert.Equal(HttpStatusCode.Unauthorized, (await Inspect(client, "{\"fields\":[\"v1\",\"safe\",\"route\"]}")).StatusCode);
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "inference.liveui");
            capability.PlatformAvailability = previousAvailability; await db.SaveChangesAsync();
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
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    private static HttpClient Client(Uri address, X509Certificate2 server, X509Certificate2 client)
    {
        var expected = server.GetCertHash(HashAlgorithmName.SHA256);
        var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256)) };
        handler.ClientCertificates.Add(client); return new HttpClient(handler) { BaseAddress = address };
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
