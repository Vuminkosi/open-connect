using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Persistence.EFCore.Navigator;

/// <summary>
/// Pure, deterministic receipt-key helpers for the EF Core Navigator receipt store transplant
/// (S0-1). Logic is byte-identical to
/// <c>OpenConnect.Navigator.Persistence.Postgres.NavigatorPersistenceMapping</c>
/// (<c>ReceiptClientTaskId</c> and <c>ReceiptTaskId</c>); only the namespace differs so the
/// EF Core adapter needs no Postgres project reference.
/// Receipt rows use the canonical <see cref="Guid"/> "D" string form for TenantScope,
/// matching <c>EfNavigatorLeaseSeam.TrySplitExecution</c>.
/// </summary>
public static class NavigatorReceiptMapping
{
    /// <summary>Derives the receipt task key. Byte-identical to the legacy Postgres mapping.</summary>
    public static string ReceiptClientTaskId(string idempotencyKey) => $"navigator:receipt:{idempotencyKey}";

    /// <summary>
    /// Stable task identity from tenant, principal, and idempotency key.
    /// Byte-identical to the legacy Postgres mapping (SHA-256, first 16 bytes, UUIDv5/v4 variant bits).
    /// </summary>
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
