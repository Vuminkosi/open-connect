using OpenConnect.Abstractions;

namespace OpenConnect.Privacy;

/// <summary>Provides the tenant identity representation required for Cloud privacy evidence reporting.</summary>
public static class CloudTenantIdentity
{
    /// <summary>Converts a nonempty Cloud tenant GUID to the equivalent host tenant ULID.</summary>
    public static TenantId ToHostTenantId(Guid cloudTenantId)
    {
        if (cloudTenantId == Guid.Empty) throw new ArgumentException("A Cloud tenant ID is required.", nameof(cloudTenantId));
        UInt128 number = 0;
        foreach (var item in cloudTenantId.ToByteArray(bigEndian: true)) number = (number << 8) | item;
        Span<char> encoded = stackalloc char[26];
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (var index = encoded.Length - 1; index >= 0; index--) { encoded[index] = alphabet[(int)(number & 31)]; number >>= 5; }
        return new TenantId(Ulid.Parse(new string(encoded)));
    }
}
