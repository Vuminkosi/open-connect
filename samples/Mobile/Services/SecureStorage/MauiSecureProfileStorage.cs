using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;
using OpenConnect.Client.Messaging;

namespace OpenConnect.Samples.Mobile.Services.SecureStorage;

/// <summary>
/// Profile-namespaced MAUI SecureStorage adapter.
/// Secret credentials are stored exclusively under <c>oc.imsg.cred.{profileId}</c> in platform
/// keystore/Keychain (MAUI SecureStorage); non-secret profile metadata is stored separately
/// in Preferences. Fails closed on SecureStorage errors.
/// </summary>
public sealed class MauiSecureProfileStorage : IMessagingProfileStorage
{
    private const string IndexKey = "oc.imsg.profile.index";
    private const string ActiveKey = "oc.imsg.profile.active";
    private const string MetaPrefix = "oc.imsg.meta.";
    private const string CredPrefix = "oc.imsg.cred.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<IReadOnlyList<MessagingProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = LoadIndex();
        var list = new List<MessagingProfileRecord>(ids.Count);
        foreach (var id in ids)
        {
            var raw = Preferences.Default.Get<string?>(MetaPrefix + id, null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<MessagingProfileRecord>(raw, JsonOptions);
            if (record is not null)
            {
                list.Add(record);
            }
        }

        IReadOnlyList<MessagingProfileRecord> ordered = list
            .OrderBy(p => p.CompanyDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ValueTask.FromResult(ordered);
    }

    public ValueTask<MessagingProfileRecord?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = Preferences.Default.Get<string?>(MetaPrefix + profileId, null);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ValueTask.FromResult<MessagingProfileRecord?>(null);
        }

        return ValueTask.FromResult(JsonSerializer.Deserialize<MessagingProfileRecord>(raw, JsonOptions));
    }

    public ValueTask SaveProfileAsync(MessagingProfileRecord profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = LoadIndex();
        if (!ids.Contains(profile.Identity.ProfileId, StringComparer.Ordinal))
        {
            ids.Add(profile.Identity.ProfileId);
            SaveIndex(ids);
        }

        Preferences.Default.Set(MetaPrefix + profile.Identity.ProfileId, JsonSerializer.Serialize(profile, JsonOptions));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = LoadIndex();
        ids.RemoveAll(id => string.Equals(id, profileId, StringComparison.Ordinal));
        SaveIndex(ids);
        Preferences.Default.Remove(MetaPrefix + profileId);
        Microsoft.Maui.Storage.SecureStorage.Default.Remove(CredPrefix + profileId);
        if (string.Equals(Preferences.Default.Get<string?>(ActiveKey, null), profileId, StringComparison.Ordinal))
        {
            Preferences.Default.Remove(ActiveKey);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<MessagingProfileCredentials?> GetCredentialsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync(CredPrefix + profileId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return JsonSerializer.Deserialize<MessagingProfileCredentials>(raw, JsonOptions);
    }

    public async ValueTask SaveCredentialsAsync(string profileId, MessagingProfileCredentials credentials, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        await Microsoft.Maui.Storage.SecureStorage.Default.SetAsync(CredPrefix + profileId, json).ConfigureAwait(false);
    }

    public ValueTask DeleteCredentialsAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Microsoft.Maui.Storage.SecureStorage.Default.Remove(CredPrefix + profileId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetActiveProfileIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Preferences.Default.Get<string?>(ActiveKey, null));
    }

    public ValueTask SetActiveProfileIdAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            Preferences.Default.Remove(ActiveKey);
        }
        else
        {
            Preferences.Default.Set(ActiveKey, profileId);
        }

        return ValueTask.CompletedTask;
    }

    private static List<string> LoadIndex()
    {
        var raw = Preferences.Default.Get<string?>(IndexKey, null);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
    }

    private static void SaveIndex(List<string> ids) =>
        Preferences.Default.Set(IndexKey, JsonSerializer.Serialize(ids, JsonOptions));
}

/// <summary>Per-profile AES-256-GCM encrypted local cache (no plaintext SQLite or localStorage).</summary>
public interface IEncryptedProfileCache
{
    ValueTask WriteEncryptedAsync(string profileId, string cacheKey, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default);
    ValueTask<byte[]?> ReadDecryptedAsync(string profileId, string cacheKey, CancellationToken cancellationToken = default);
    ValueTask WipeProfileCacheAsync(string profileId, CancellationToken cancellationToken = default);
}

public sealed class MauiEncryptedProfileCache : IEncryptedProfileCache
{
    private const string CacheKeyPrefix = "oc.imsg.cachekey.";

    public async ValueTask WriteEncryptedAsync(string profileId, string cacheKey, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = await GetOrCreateProfileKeyAsync(profileId).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var aad = Encoding.UTF8.GetBytes($"{profileId}:{cacheKey}");

        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, aad);
        }

        var path = GetCacheFilePath(profileId, cacheKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, 12);
        Buffer.BlockCopy(tag, 0, payload, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, payload, 28, ciphertext.Length);
        await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReadDecryptedAsync(string profileId, string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetCacheFilePath(profileId, cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var keyRaw = await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync(CacheKeyPrefix + profileId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(keyRaw))
        {
            return null;
        }

        var key = Convert.FromBase64String(keyRaw);
        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return DecryptPayload(key, payload, profileId, cacheKey);
    }

    private static byte[]? DecryptPayload(byte[] key, byte[] payload, string profileId, string cacheKey)
    {
        if (payload.Length < 28)
        {
            return null;
        }

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var ciphertext = payload.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        var aad = Encoding.UTF8.GetBytes($"{profileId}:{cacheKey}");

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    public ValueTask WipeProfileCacheAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Microsoft.Maui.Storage.SecureStorage.Default.Remove(CacheKeyPrefix + profileId);
        var dir = Path.Combine(FileSystem.Current.CacheDirectory, "oc-imsg", Sanitize(profileId));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask<byte[]> GetOrCreateProfileKeyAsync(string profileId)
    {
        var existing = await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync(CacheKeyPrefix + profileId).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return Convert.FromBase64String(existing);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        await Microsoft.Maui.Storage.SecureStorage.Default.SetAsync(CacheKeyPrefix + profileId, Convert.ToBase64String(key)).ConfigureAwait(false);
        return key;
    }

    private static string GetCacheFilePath(string profileId, string cacheKey) =>
        Path.Combine(FileSystem.Current.CacheDirectory, "oc-imsg", Sanitize(profileId), Sanitize(cacheKey) + ".enc");

    private static string Sanitize(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
