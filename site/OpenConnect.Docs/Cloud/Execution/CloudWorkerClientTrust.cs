using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudWorkerCertificateBinding(Guid DeploymentId, string ClientCertificateSha256);

// Certificate pins are deployment configuration, never request-controlled data.
public sealed class CloudWorkerClientTrust
{
    private readonly (Guid DeploymentId, byte[] Pin)[] _bindings;

    public CloudWorkerClientTrust(IEnumerable<CloudWorkerCertificateBinding> bindings, CloudGatewayClientTrust? gatewayTrust)
    {
        var values = bindings.ToArray();
        if (values.Length is < 1 or > 128) throw new InvalidOperationException("Configure one to 128 worker certificate bindings.");
        try
        {
            _bindings = values.Select(x => (x.DeploymentId, Convert.FromHexString(x.ClientCertificateSha256))).ToArray();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidOperationException("Worker certificate fingerprints must be SHA-256 hex.");
        }
        if (_bindings.Any(x => x.DeploymentId == Guid.Empty || x.Pin.Length != 32 || gatewayTrust?.ContainsPin(x.Pin) == true) ||
            _bindings.Select(x => Convert.ToHexString(x.Pin)).Distinct(StringComparer.Ordinal).Count() != _bindings.Length)
            throw new InvalidOperationException("Worker certificate bindings must be unique and separate from gateway certificates.");
    }

    public Guid? ResolveDeployment(X509Certificate2? certificate, DateTimeOffset now)
    {
        if (certificate is null || now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()) return null;
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (usages.Length != 1 || !usages[0].EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2")) return null;
        var hash = certificate.GetCertHash(HashAlgorithmName.SHA256);
        foreach (var (deploymentId, pin) in _bindings)
            if (CryptographicOperations.FixedTimeEquals(pin, hash)) return deploymentId;
        return null;
    }

    internal bool Overlaps(CloudGatewayClientTrust trust) => _bindings.Any(binding => trust.ContainsPin(binding.Pin));
}
