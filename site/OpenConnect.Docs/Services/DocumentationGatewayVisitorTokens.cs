using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;

namespace OpenConnect.Docs.Services;

/// <summary>Issues and verifies short-lived identities for anonymous documentation visitors.</summary>
public sealed class DocumentationGatewayVisitorTokens
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly byte[] _key;
    private readonly string _tenant;
    private readonly TimeProvider _clock;

    public DocumentationGatewayVisitorTokens(string tenant, string signingKey, TimeProvider? clock = null)
    {
        _tenant = CanonicalId(tenant) ?? throw new ArgumentException("A valid documentation tenant is required.", nameof(tenant));
        try { _key = Convert.FromBase64String(signingKey); }
        catch (FormatException) { throw new ArgumentException("The visitor signing key must be base64.", nameof(signingKey)); }
        if (_key.Length != 32 || Convert.ToBase64String(_key) != signingKey)
            throw new ArgumentException("The visitor signing key must contain 32 bytes in canonical base64.", nameof(signingKey));
        _clock = clock ?? TimeProvider.System;
    }

    public string Issue()
    {
        var principal = Ulid.NewUlid().ToString();
        var expiry = _clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var payload = $"v1.{_tenant}.{principal}.{expiry.ToString(CultureInfo.InvariantCulture)}";
        return payload + "." + Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload)));
    }

    public bool TryValidate(string? token, out string tenant, out string principal)
    {
        tenant = string.Empty;
        principal = string.Empty;
        if (token is null || token.Length > 180) return false;
        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1" || parts[1] != _tenant ||
            CanonicalId(parts[2]) is null || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry) ||
            parts[4].Length != 64) return false;
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (expiry <= now || expiry > now + (long)Lifetime.TotalSeconds) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(parts[4]); }
        catch (FormatException) { return false; }
        var payload = string.Join('.', parts.AsSpan(0, 4).ToArray());
        var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return false;
        tenant = _tenant;
        principal = parts[2];
        return true;
    }

    private static string? CanonicalId(string? value)
    {
        if (value is null || value.Length != 26) return null;
        try
        {
            var id = Ulid.Parse(value);
            return id != default && id.ToString() == value ? value : null;
        }
        catch (FormatException) { return null; }
    }
}
