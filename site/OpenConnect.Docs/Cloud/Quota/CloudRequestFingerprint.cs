using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudFingerprintInput(Guid TenantId, Guid ApplicationId, string CapabilityId,
    string Method, string Route, string MediaType, ReadOnlyMemory<byte> Body);

public static class CloudRequestFingerprint
{
    public static bool IsValidKeyId(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    public static byte[] Compute(string keyId, ReadOnlySpan<byte> key, CloudFingerprintInput input)
    {
        if (!IsValidKeyId(keyId) || key.Length < 32 || input.TenantId == Guid.Empty || input.ApplicationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.CapabilityId) || input.CapabilityId.Length > 80 ||
            string.IsNullOrWhiteSpace(input.Method) || input.Method.Length > 16 ||
            string.IsNullOrWhiteSpace(input.Route) || input.Route.Length > 2048 ||
            string.IsNullOrWhiteSpace(input.MediaType) || input.MediaType.Length > 255)
            throw new ArgumentException("Invalid request fingerprint inputs.");
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        Add(hmac, Encoding.UTF8.GetBytes("openconnect-cloud-request-v1"));
        Add(hmac, Encoding.UTF8.GetBytes(keyId));
        Add(hmac, Encoding.UTF8.GetBytes(input.TenantId.ToString("N")));
        Add(hmac, Encoding.UTF8.GetBytes(input.ApplicationId.ToString("N")));
        Add(hmac, Encoding.UTF8.GetBytes(input.CapabilityId));
        Add(hmac, Encoding.UTF8.GetBytes(input.Method));
        Add(hmac, Encoding.UTF8.GetBytes(input.Route));
        Add(hmac, Encoding.UTF8.GetBytes(input.MediaType));
        Add(hmac, input.Body.Span);
        return hmac.GetHashAndReset();
    }

    private static void Add(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(size, value.Length);
        hash.AppendData(size);
        hash.AppendData(value);
    }
}
