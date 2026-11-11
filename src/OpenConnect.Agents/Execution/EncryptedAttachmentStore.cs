using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenConnect.Agents.Execution;

/// <summary>An immutable, tenant-scoped reference to an encrypted attachment stored out-of-band.</summary>
/// <param name="AttachmentId">Unique identifier of the attachment.</param>
/// <param name="TenantId">Owning tenant identifier.</param>
/// <param name="Sha256Hash">Cryptographic hash of the original plaintext payload.</param>
/// <param name="ByteLength">Size in bytes of the original plaintext.</param>
/// <param name="ContentType">Media MIME type or classification.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
public sealed record AttachmentObjectRef(
    string AttachmentId,
    string TenantId,
    string Sha256Hash,
    long ByteLength,
    string ContentType,
    DateTimeOffset CreatedAt);

/// <summary>Stores attachments as encrypted, tenant-scoped object references instead of embedding them in prompts or message payloads.</summary>
public interface IAgentAttachmentStore
{
    /// <summary>Encrypts and stores attachment bytes, returning a lightweight tenant-scoped reference.</summary>
    ValueTask<AttachmentObjectRef> StoreAsync(
        string tenantId,
        byte[] payload,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves and decrypts attachment bytes for an authorized tenant.</summary>
    ValueTask<byte[]> RetrieveAsync(
        string tenantId,
        string attachmentId,
        CancellationToken cancellationToken = default);
}

/// <summary>Process-local AES-GCM encrypted attachment store for testing and in-memory workflows.</summary>
public sealed class InMemoryEncryptedAttachmentStore : IAgentAttachmentStore
{
    private sealed record EncryptedBlob(
        AttachmentObjectRef Reference,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);

    private readonly ConcurrentDictionary<(string TenantId, string AttachmentId), EncryptedBlob> _storage = new();
    private readonly byte[] _encryptionKey;

    /// <summary>Initializes a new store using a randomly generated 256-bit AES key.</summary>
    public InMemoryEncryptedAttachmentStore()
    {
        _encryptionKey = new byte[32];
        RandomNumberGenerator.Fill(_encryptionKey);
    }

    /// <inheritdoc />
    public ValueTask<AttachmentObjectRef> StoreAsync(
        string tenantId,
        byte[] payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        cancellationToken.ThrowIfCancellationRequested();

        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var id = Guid.NewGuid().ToString("N");
        var reference = new AttachmentObjectRef(id, tenantId, hash, payload.LongLength, contentType, DateTimeOffset.UtcNow);

        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[payload.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(_encryptionKey, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Encrypt(nonce, payload, ciphertext, tag);
        }

        var blob = new EncryptedBlob(reference, nonce, ciphertext, tag);
        _storage[(tenantId, id)] = blob;

        return ValueTask.FromResult(reference);
    }

    /// <inheritdoc />
    public ValueTask<byte[]> RetrieveAsync(
        string tenantId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_storage.TryGetValue((tenantId, attachmentId), out var blob))
            throw new KeyNotFoundException($"Attachment '{attachmentId}' not found for tenant '{tenantId}'.");

        if (!string.Equals(blob.Reference.TenantId, tenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Cross-tenant attachment retrieval is denied.");

        var plaintext = new byte[blob.Ciphertext.Length];
        using (var aes = new AesGcm(_encryptionKey, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Decrypt(blob.Nonce, blob.Ciphertext, blob.Tag, plaintext);
        }

        return ValueTask.FromResult(plaintext);
    }
}
