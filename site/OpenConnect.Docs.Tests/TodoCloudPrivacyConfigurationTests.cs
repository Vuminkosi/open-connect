using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class TodoCloudPrivacyConfigurationTests
{
    [Fact]
    public void Disabled_reporting_does_not_require_credentials_or_create_services()
    {
        var services = new ServiceCollection();
        TodoCloudPrivacyConfiguration.Configure(services, Configuration(new()));
        Assert.Empty(services);
    }

    [Fact]
    public void Enabled_reporting_requires_secure_complete_configuration_without_echoing_credentials()
    {
        var values = new Dictionary<string, string?> {
            ["OpenConnect:CloudPrivacy:Enabled"] = "true",
            ["OpenConnect:CloudPrivacy:Origin"] = "http://cloud.example/",
            ["OpenConnect:CloudPrivacy:Credential"] = "sensitive-credential",
            ["OpenConnect:CloudPrivacy:CertificatePath"] = "missing.pfx"
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            TodoCloudPrivacyConfiguration.Configure(new ServiceCollection(), Configuration(values)));
        Assert.DoesNotContain("sensitive-credential", error.ToString());
        values["OpenConnect:CloudPrivacy:Origin"] = "https://cloud.example/";
        values["OpenConnect:CloudPrivacy:ServerCertificateSha256"] = "invalid";
        Assert.Throws<InvalidOperationException>(() => TodoCloudPrivacyConfiguration.Configure(new ServiceCollection(), Configuration(values)));
    }

    [Fact]
    public async Task Enabled_reporting_registers_recording_firewall_and_cloud_sink()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), $"oc-privacy-{Guid.NewGuid():N}.pfx");
        try
        {
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "test-password"));
            var values = new Dictionary<string, string?> {
                ["OpenConnect:CloudPrivacy:Enabled"] = "true",
                ["OpenConnect:CloudPrivacy:Origin"] = "https://cloud.example/",
                ["OpenConnect:CloudPrivacy:Credential"] = "fixture-token",
                ["OpenConnect:CloudPrivacy:CertificatePath"] = path,
                ["OpenConnect:CloudPrivacy:CertificatePassword"] = "test-password",
                ["OpenConnect:CloudPrivacy:ServerCertificateSha256"] = new string('a', 64)
            };
            var services = new ServiceCollection();
            TodoCloudPrivacyConfiguration.Configure(services, Configuration(values));
            using (var provider = services.BuildServiceProvider())
                Assert.IsType<CloudPrivacyEvidenceSink>(provider.GetRequiredService<IHostPrivacyEvidenceSink>());
            // Recreate the configuration because the first provider owns its certificate.
            services = new ServiceCollection();
            TodoCloudPrivacyConfiguration.Configure(services, Configuration(values));
            var sink = new RecordingSink();
            services.AddSingleton<IHostPrivacyEvidenceSink>(sink);
            using var providerWithRecording = services.BuildServiceProvider();
            var firewall = Assert.IsType<EvidenceRecordingPrivacyFirewall>(providerWithRecording.GetRequiredService<IPrivacyFirewall>());
            var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
            var decision = await firewall.EvaluateAsync("Contact person@example.com", context);
            Assert.DoesNotContain("person@example.com", decision.SanitizedContent ?? string.Empty, StringComparison.Ordinal);
            var evidence = Assert.Single(sink.Events);
            Assert.Equal(1, evidence.Categories["EMAIL"]);
            Assert.Equal(EgressDestination.Output, evidence.Destination);
            Assert.DoesNotContain("person@example.com", System.Text.Json.JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class RecordingSink : IHostPrivacyEvidenceSink
    {
        public List<HostPrivacyEvidence> Events { get; } = [];
        public ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
        {
            Events.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }
}
