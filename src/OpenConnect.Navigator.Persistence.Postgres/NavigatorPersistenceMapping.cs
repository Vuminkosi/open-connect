using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>Pure, deterministic helpers for the approved PostgreSQL Navigator identity mapping.</summary>
public static class NavigatorPersistenceMapping
{
    /// <summary>Maps canonical UUID text (D/N formats) to its database identifier.</summary>
    public static bool TryUuid(string? value, out Guid id) => Guid.TryParse(value, out id);

    /// <summary>
    /// Derives the receipt task key. Option B deliberately requires UUID tenant/principal IDs;
    /// the in-memory adapter remains available for arbitrary development/test identifiers.
    /// </summary>
    public static string ReceiptClientTaskId(string idempotencyKey) => $"navigator:receipt:{idempotencyKey}";

    /// <summary>Derives the operational task key for a Navigator session.</summary>
    public static string SessionClientTaskId(Guid sessionId) => $"navigator:{sessionId:N}";

    /// <summary>Stable task identity from tenant, principal, and idempotency key.</summary>
    public static Guid ReceiptTaskId(Guid tenant, Guid principal, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var material = Encoding.UTF8.GetBytes($"{tenant:D}|{principal:D}|{idempotencyKey}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(material, hash);
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
