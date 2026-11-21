using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Samples.Blazor;

/// <summary>Configures explicitly enabled Cloud reporting of content-free privacy evidence.</summary>
internal static class TodoCloudPrivacyConfiguration
{
    internal static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("OpenConnect:CloudPrivacy:Enabled")) return;
        var origin = configuration["OpenConnect:CloudPrivacy:Origin"];
        var token = configuration["OpenConnect:CloudPrivacy:Credential"];
        var certificatePath = configuration["OpenConnect:CloudPrivacy:CertificatePath"];
        var password = configuration["OpenConnect:CloudPrivacy:CertificatePassword"];
        var serverPin = configuration["OpenConnect:CloudPrivacy:ServerCertificateSha256"];
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps ||
            authority.AbsolutePath != "/" || !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment) ||
            string.IsNullOrWhiteSpace(token) || token.Any(character => character < 33 || character > 126) ||
            string.IsNullOrWhiteSpace(certificatePath) ||
            !string.IsNullOrEmpty(serverPin) && (serverPin.Length != 64 || !serverPin.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("Cloud privacy reporting configuration is invalid.");
        using (LoadCertificate(certificatePath, password)) { }
        services.AddSingleton(_ => LoadCertificate(certificatePath, password));
        services.AddOpenConnectRulesDetector();
        services.EnsureHostPrivacyServices();
        services.AddSingleton<IHostPrivacyEvidenceSink>(provider => string.IsNullOrEmpty(serverPin)
            ? new CloudPrivacyEvidenceSink(authority, provider.GetRequiredService<X509Certificate2>(), token)
            : new CloudPrivacyEvidenceSink(authority, provider.GetRequiredService<X509Certificate2>(), token, serverPin));
        services.AddRecordedPrivacyFirewall(provider => new PrivacyFirewall(
            provider.GetRequiredService<IOptions<PrivacyOptions>>(), provider.GetRequiredService<PolicyEngine>()));
    }

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException();
            }
            return certificate;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException("Cloud privacy reporting configuration is invalid."); }
    }
}
