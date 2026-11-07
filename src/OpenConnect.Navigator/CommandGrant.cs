using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenConnect.Navigator;

/// <summary>A narrowly scoped authorization to execute one command once before expiry.</summary>
public sealed record CommandGrant(
    string CommandId,
    string NavigatorSessionId,
    long Sequence,
    NavigatorActionType Action,
    string TargetId,
    long TargetRevision,
    string ArgsHash,
    string Tenant,
    string Principal,
    string ClientInstance,
    long PolicyRevision,
    DateTimeOffset ExpiresAt,
    string Nonce)
{
    /// <summary>Returns whether the grant's identity bindings and normalized argument digest match.</summary>
    public bool IsValidFor(string sessionId, string tenant, string principal, string clientInstance,
        string commandId, long sequence, NavigatorActionType action, string targetId, long targetRevision,
        long policyRevision, object? args, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(CommandId) || string.IsNullOrWhiteSpace(NavigatorSessionId) ||
            string.IsNullOrWhiteSpace(TargetId) || string.IsNullOrWhiteSpace(Nonce) ||
            Sequence < 0 || TargetRevision < 1 || PolicyRevision < 1 || !Enum.IsDefined(Action) || ExpiresAt <= now)
            return false;

        return NavigatorSessionId == sessionId && Tenant == tenant && Principal == principal &&
            ClientInstance == clientInstance && CommandId == commandId && Sequence == sequence &&
            Action == action && TargetId == targetId && TargetRevision == targetRevision &&
            PolicyRevision == policyRevision && DigestMatches(ArgsHash, ComputeArgsHash(args));
    }

    /// <summary>Computes lowercase SHA-256 over the normalized JSON representation of arguments.</summary>
    public static string ComputeArgsHash(object? args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        var normalized = Normalize(document.RootElement);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Normalize(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Normalize)) + "]",
        _ => element.GetRawText()
    };

    private static bool DigestMatches(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }
}

/// <summary>An exact-digest approval that can be consumed once and only before expiry.</summary>
public sealed class NavigatorApproval(string digest, DateTimeOffset expiresAt)
{
    private int _consumed;

    /// <summary>Consumes this approval if its digest matches and it has not expired or been used.</summary>
    public bool TryConsume(string requestedDigest, DateTimeOffset now)
    {
        if (expiresAt <= now || !FixedDigestEquals(digest, requestedDigest)) return false;
        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }

    private static bool FixedDigestEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
}
