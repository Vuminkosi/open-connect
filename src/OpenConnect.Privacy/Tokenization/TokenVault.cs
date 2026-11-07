using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Privacy.Tokenization;

/// <summary>Encrypted, process-local token vault. Supply a stable protected key to preserve entries across instances.</summary>
public sealed class TokenVault : ITokenVault, IDisposable
{
    private sealed record Entry(byte[] Nonce, byte[] Ciphertext, byte[] Tag, string EntityType, PrivacyScope Scope, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an encrypted in-process vault using the supplied AES key.</summary>
    /// <param name="key">AES key containing 16, 24, or 32 bytes.</param>
    /// <param name="timeProvider">Optional time source used to determine token expiry.</param>
    /// <exception cref="ArgumentNullException">The key is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key length is not valid for AES.</exception>
    public TokenVault(byte[] key, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is not (16 or 24 or 32)) throw new ArgumentException("AES key must be 128, 192, or 256 bits.", nameof(key));
        _key = key.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask<string> StoreAsync(string value, string entityType, PrivacyScope scope, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        scope.Validate();
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        var token = "[OC_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "]";
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var associatedData = Encoding.UTF8.GetBytes(token + "\0" + entityType + "\0" + scope.Tenant + "\0" + scope.Principal + "\0" + scope.Session + "\0" + scope.Purpose);
        using (var aes = new AesGcm(_key, tag.Length)) aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(associatedData);
        _entries[token] = new Entry(nonce, ciphertext, tag, entityType, scope, _timeProvider.GetUtcNow().Add(ttl));
        return ValueTask.FromResult(token);
    }

    /// <inheritdoc />
    public ValueTask<string?> ResolveAsync(string token, PrivacyScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scope.Validate();
        if (!_entries.TryGetValue(token, out var entry) || entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _entries.TryRemove(token, out _);
            return ValueTask.FromResult<string?>(null);
        }
        if (entry.Scope != scope) return ValueTask.FromResult<string?>(null);
        var plaintext = new byte[entry.Ciphertext.Length];
        var associatedData = Encoding.UTF8.GetBytes(token + "\0" + entry.EntityType + "\0" + scope.Tenant + "\0" + scope.Principal + "\0" + scope.Session + "\0" + scope.Purpose);
        try
        {
            using var aes = new AesGcm(_key, entry.Tag.Length);
            aes.Decrypt(entry.Nonce, entry.Ciphertext, entry.Tag, plaintext, associatedData);
            return ValueTask.FromResult<string?>(Encoding.UTF8.GetString(plaintext));
        }
        catch (CryptographicException) { return ValueTask.FromResult<string?>(null); }
        finally { CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(associatedData); }
    }

    /// <summary>Clears the in-memory encryption key.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
