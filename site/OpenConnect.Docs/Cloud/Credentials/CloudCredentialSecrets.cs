using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Docs.Cloud;

internal sealed record CloudCredentialMaterial(Guid Id, byte[] Hash, string Fingerprint, string Secret);

internal static class CloudCredentialSecrets
{
    internal static CloudCredentialMaterial Create(string environment)
    {
        var id = Guid.NewGuid();
        var randomSecret = RandomNumberGenerator.GetBytes(32);
        string token;
        try
        {
            token = $"oc_{EnvironmentTag(environment)}_{id:N}_{Convert.ToBase64String(randomSecret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        }
        finally { CryptographicOperations.ZeroMemory(randomSecret); }
        var bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash;
        try { hash = SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return new(id, hash, Convert.ToHexString(hash.AsSpan(0, 6)), token);
    }

    internal static bool TryReadId(string? token, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 160) return false;
        var parts = token.Split('_', 4);
        return parts.Length == 4 && parts[0] == "oc" && Guid.TryParseExact(parts[2], "N", out id) &&
            parts[3].Length >= 40 && parts[3].Length <= 48;
    }

    internal static bool HasExpectedEnvironmentTag(string token, string environment) =>
        token.StartsWith($"oc_{EnvironmentTag(environment)}_", StringComparison.Ordinal);

    internal static bool Matches(string token, byte[] expectedHash)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash;
        try { hash = SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        try { return CryptographicOperations.FixedTimeEquals(hash, expectedHash); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private static string EnvironmentTag(string environment) => environment switch
    {
        "Development" => "dev",
        "Staging" => "stg",
        "Production" => "prd",
        _ => throw new InvalidOperationException("The application environment is invalid."),
    };
}
