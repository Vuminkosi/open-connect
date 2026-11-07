using OpenConnect.Abstractions;

namespace OpenConnect.Persistence.EFCore.Stores;

/// <summary>Implements the EF-02D master-approved interim mapping from ULID's canonical representation to Guid.</summary>
internal static class UlidGuidMapper
{
    public static Guid ToGuid(Ulid value)
    {
        // EF-02D master-approved interim mapping: decode canonical Crockford Base32 into big-endian 128-bit bytes.
        UInt128 number = 0;
        foreach (var c in value.ToString())
        {
            var digit = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(c);
            if (digit < 0) throw new FormatException("Invalid canonical ULID.");
            number = (number << 5) | (uint)digit;
        }
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 15; i >= 0; i--) { bytes[i] = (byte)number; number >>= 8; }
        return new Guid(bytes, bigEndian: true);
    }

    public static string Scope(Ulid value) => value.ToString();

    public static Ulid FromGuid(Guid value)
    {
        var bytes = value.ToByteArray(bigEndian: true);
        UInt128 number = 0;
        foreach (var b in bytes) number = (number << 8) | b;
        Span<char> encoded = stackalloc char[26];
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (var i = 25; i >= 0; i--) { encoded[i] = alphabet[(int)(number & 31)]; number >>= 5; }
        return Ulid.Parse(new string(encoded));
    }
}
