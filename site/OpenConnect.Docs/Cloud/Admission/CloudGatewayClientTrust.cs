using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudGatewayClientTrust
{
    private readonly byte[][] _pins;

    public CloudGatewayClientTrust(IEnumerable<string> fingerprints)
    {
        var values = fingerprints.ToArray();
        if (values.Length is < 1 or > 16) throw new InvalidOperationException("Configure one to sixteen gateway client certificate fingerprints.");
        try { _pins = values.Select(Convert.FromHexString).ToArray(); }
        catch (FormatException) { throw new InvalidOperationException("Gateway client certificate fingerprints must be SHA-256 hex."); }
        if (_pins.Any(x => x.Length != 32)) throw new InvalidOperationException("Gateway client certificate fingerprints must be SHA-256 hex.");
    }

    public void ConfigureHttps(HttpsConnectionAdapterOptions https)
    {
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.ClientCertificateValidation = (certificate, _, _) => IsTrusted(certificate, DateTimeOffset.UtcNow);
    }

    public bool IsTrusted(X509Certificate2? certificate, DateTimeOffset now)
    {
        if (certificate is null || now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()) return false;
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (usages.Length != 1 || !usages[0].EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2")) return false;
        var hash = certificate.GetCertHash(HashAlgorithmName.SHA256);
        return ContainsPin(hash);
    }

    public bool ContainsPin(ReadOnlySpan<byte> hash)
    {
        foreach (var pin in _pins)
            if (CryptographicOperations.FixedTimeEquals(pin, hash)) return true;
        return false;
    }
}
