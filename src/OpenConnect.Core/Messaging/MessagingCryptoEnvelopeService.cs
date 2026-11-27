using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Core.Messaging;

/// <summary>
/// Standards-based cryptographic envelope and device-identity service using ECDH P-256,
/// HKDF-SHA256, AES-256-GCM authenticated encryption, and ECDSA P-256 proof-of-possession.
/// Implements <see cref="IMessagingCryptoProvider"/> as the explicit extension seam for
/// RFC 9420 MLS / OpenMLS native bindings. Never substitutes plain TLS for E2EE.
/// </summary>
public sealed class MessagingCryptoEnvelopeService : IMessagingCryptoProvider
{
    public const string CanonicalProtocolName = "openconnect.e2ee.p256-hkdf-aes256gcm.v1";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly ConcurrentDictionary<ConversationId, GroupCryptoContext> _groups = new();
    private readonly ConcurrentDictionary<KeyPackageId, bool> _consumedPackages = new();
    private readonly TimeProvider _timeProvider;

    public MessagingCryptoEnvelopeService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ProtocolName => CanonicalProtocolName;

    /// <summary>Generates a new ECDSA/ECDH P-256 device identity key pair (SPKI public, PKCS#8 private).</summary>
    public static (string PublicKeyBase64, string PrivateKeyPkcs8Base64) GenerateDeviceKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdsa.ExportSubjectPublicKeyInfo();
        var privateKey = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            return (Convert.ToBase64String(publicKey), Convert.ToBase64String(privateKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>Signs a challenge payload using the device's P-256 PKCS#8 private key for proof-of-possession.</summary>
    public static byte[] SignProofOfPossession(string privateKeyPkcs8Base64, ReadOnlySpan<byte> challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPkcs8Base64);
        var keyBytes = Convert.FromBase64String(privateKeyPkcs8Base64);
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
            return ecdsa.SignData(challenge, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>Creates a single-use P-256 ECDH public key package for a device.</summary>
    public static DeviceKeyPackage CreateKeyPackage(
        TenantId tenantId,
        DeviceId deviceId,
        PrincipalId? ownerPrincipalId = null,
        AgentId? ownerAgentId = null,
        DateTimeOffset? createdAt = null,
        TimeSpan? lifetime = null)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdh.ExportSubjectPublicKeyInfo();
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var ttl = lifetime ?? TimeSpan.FromDays(7);
        return new DeviceKeyPackage(
            tenantId,
            new KeyPackageId(Ulid.NewUlid()),
            ownerPrincipalId,
            ownerAgentId,
            deviceId,
            CanonicalProtocolName,
            Convert.ToBase64String(spki),
            now,
            now.Add(ttl));
    }

    /// <summary>
    /// Deterministic HKDF-SHA256 epoch key derivation helper exposed for known-answer test vector verification.
    /// </summary>
    public static byte[] DeriveEpochKeyDeterministic(
        ReadOnlySpan<byte> inputKeyMaterial,
        ReadOnlySpan<byte> salt,
        ConversationId conversationId,
        long epoch)
    {
        if (inputKeyMaterial.Length < KeySizeBytes)
            throw new ArgumentException("Input key material must be at least 32 bytes.", nameof(inputKeyMaterial));
        var info = Encoding.UTF8.GetBytes($"{CanonicalProtocolName}|epoch|{conversationId.Value}|{epoch.ToString(CultureInfo.InvariantCulture)}");
        var output = new byte[KeySizeBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, output, salt, info);
        return output;
    }

    public ValueTask<bool> VerifyDeviceProofOfPossessionAsync(
        string publicKeyBase64,
        ReadOnlyMemory<byte> challenge,
        ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || challenge.IsEmpty || signature.IsEmpty)
            return ValueTask.FromResult(false);

        try
        {
            var pubBytes = Convert.FromBase64String(publicKeyBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubBytes, out _);
            var valid = ecdsa.VerifyData(challenge.Span, signature.Span, HashAlgorithmName.SHA256);
            return ValueTask.FromResult(valid);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<string> ComputeKeyFingerprintAsync(
        string publicKeyBase64,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyBase64);
        var pubBytes = Convert.FromBase64String(publicKeyBase64);
        var hash = SHA256.HashData(pubBytes);
        var hex = Convert.ToHexString(hash.AsSpan(0, 16));
        var formatted = string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
        return ValueTask.FromResult(formatted);
    }

    public ValueTask<MessagingCryptoGroupState> CreateGroupAsync(
        ConversationId conversationId,
        IReadOnlyList<DeviceKeyPackage> initialMemberPackages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialMemberPackages);
        if (initialMemberPackages.Count == 0)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "At least one device key package is required.", 400);

        var now = _timeProvider.GetUtcNow();
        var ikmList = new List<byte[]>(initialMemberPackages.Count + 1)
        {
            RandomNumberGenerator.GetBytes(KeySizeBytes)
        };

        var windows = new Dictionary<DeviceId, DeviceEpochWindow>();
        foreach (var pkg in initialMemberPackages)
        {
            ConsumeAndValidateKeyPackage(pkg, now);
            ikmList.Add(DeriveEphemeralSharedSecret(pkg.PublicPackageBase64));
            windows[pkg.DeviceId] = new DeviceEpochWindow(JoinedEpoch: 1, RemovedEpoch: null);
        }

        var epochKey = DeriveGroupEpochKey(conversationId, epoch: 1, ikmList);
        ZeroAll(ikmList);

        var ctx = new GroupCryptoContext(conversationId, currentEpoch: 1, epochKey, windows, now);
        if (!_groups.TryAdd(conversationId, ctx))
            throw new MessagingDomainException(ErrorCodes.Conflict, "Cryptographic group already exists for this conversation.", 409);

        return ValueTask.FromResult(ctx.ToState(CanonicalProtocolName));
    }

    public ValueTask<MessagingCryptoGroupState> AddDeviceAndRekeyAsync(
        ConversationId conversationId,
        DeviceKeyPackage newMemberPackage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newMemberPackage);
        if (!_groups.TryGetValue(conversationId, out var ctx))
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Cryptographic group was not found.", 404);

        var now = _timeProvider.GetUtcNow();
        ConsumeAndValidateKeyPackage(newMemberPackage, now);
        var sharedSecret = DeriveEphemeralSharedSecret(newMemberPackage.PublicPackageBase64);
        var entropy = RandomNumberGenerator.GetBytes(KeySizeBytes);

        try
        {
            lock (ctx.Gate)
            {
                var nextEpoch = ctx.CurrentEpoch + 1;
                var nextKey = DeriveGroupEpochKey(conversationId, nextEpoch, [entropy, sharedSecret]);
                ctx.AdvanceEpoch(nextEpoch, nextKey, now);
                // Newly added device can only decrypt from nextEpoch onward (cannot decrypt prior history!).
                ctx.MemberWindows[newMemberPackage.DeviceId] = new DeviceEpochWindow(JoinedEpoch: nextEpoch, RemovedEpoch: null);
                return ValueTask.FromResult(ctx.ToState(CanonicalProtocolName));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public ValueTask<MessagingCryptoGroupState> RemoveDeviceAndRekeyAsync(
        ConversationId conversationId,
        DeviceId removedDeviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_groups.TryGetValue(conversationId, out var ctx))
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Cryptographic group was not found.", 404);

        var now = _timeProvider.GetUtcNow();
        var entropy = RandomNumberGenerator.GetBytes(KeySizeBytes);
        try
        {
            lock (ctx.Gate)
            {
                if (!ctx.MemberWindows.TryGetValue(removedDeviceId, out var existing) || existing.RemovedEpoch is not null)
                    throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Device is not an active member of this cryptographic group.", 404);

                var nextEpoch = ctx.CurrentEpoch + 1;
                // Fresh uncorrelated key material so the removed device cannot derive or decrypt subsequent epochs.
                var nextKey = DeriveGroupEpochKey(conversationId, nextEpoch, [entropy]);
                ctx.AdvanceEpoch(nextEpoch, nextKey, now);
                ctx.MemberWindows[removedDeviceId] = existing with { RemovedEpoch = nextEpoch };
                return ValueTask.FromResult(ctx.ToState(CanonicalProtocolName));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public ValueTask<EncryptedMessageEnvelope> EncryptAsync(
        ConversationId conversationId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default)
    {
        if (!_groups.TryGetValue(conversationId, out var ctx))
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Cryptographic group was not found.", 404);

        long epoch;
        byte[] keyCopy;
        lock (ctx.Gate)
        {
            if (!ctx.IsDeviceAuthorizedForEpoch(senderDeviceId, ctx.CurrentEpoch))
                throw new MessagingDomainException(ErrorCodes.Unauthorized, "Sender device is not authorized for the current cryptographic epoch.", 403);

            epoch = ctx.CurrentEpoch;
            keyCopy = (byte[])ctx.EpochKeys[epoch].Clone();
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        var boundAad = BuildBoundAssociatedData(conversationId, epoch, senderDeviceId, associatedData.Span);

        try
        {
            using var aes = new AesGcm(keyCopy, TagSizeBytes);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, boundAad);

            return ValueTask.FromResult(new EncryptedMessageEnvelope(
                CanonicalProtocolName,
                epoch,
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                associatedData.IsEmpty ? null : Convert.ToBase64String(associatedData.Span),
                senderDeviceId.Value.ToString()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    public ValueTask<byte[]> DecryptAsync(
        ConversationId conversationId,
        DeviceId recipientDeviceId,
        EncryptedMessageEnvelope envelope,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.Protocol, CanonicalProtocolName, StringComparison.Ordinal))
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Unsupported or mismatched cryptographic protocol.", 400);

        if (!_groups.TryGetValue(conversationId, out var ctx))
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Cryptographic group was not found.", 404);

        if (string.IsNullOrWhiteSpace(envelope.SenderKeyId))
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Sender key identifier is missing.", 400);

        DeviceId senderDeviceId;
        try
        {
            senderDeviceId = new DeviceId(Ulid.Parse(envelope.SenderKeyId));
        }
        catch (FormatException)
        {
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Sender key identifier is malformed.", 400);
        }

        byte[] keyCopy;
        var replayKey = $"{recipientDeviceId.Value}|{envelope.CryptoEpoch}|{envelope.NonceBase64}";
        lock (ctx.Gate)
        {
            if (!ctx.EpochKeys.TryGetValue(envelope.CryptoEpoch, out var epochKey))
                throw new MessagingDomainException(ErrorCodes.CryptoEpochMismatch, "Cryptographic epoch is unknown.", 409);

            if (!ctx.IsDeviceAuthorizedForEpoch(recipientDeviceId, envelope.CryptoEpoch))
                throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Device is not authorized to decrypt this cryptographic epoch.", 403);

            if (ctx.SeenNonces.Contains(replayKey))
                throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Replayed encrypted envelope rejected.", 409);

            keyCopy = (byte[])epochKey.Clone();
        }

        byte[] ciphertext;
        byte[] nonce;
        byte[] tag;
        try
        {
            ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
            nonce = Convert.FromBase64String(envelope.NonceBase64);
            tag = Convert.FromBase64String(envelope.AuthTagBase64);
        }
        catch (FormatException)
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Encrypted envelope fields are not valid Base64.", 400);
        }

        if (nonce.Length != NonceSizeBytes || tag.Length != TagSizeBytes)
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Nonce or authentication tag length is invalid.", 400);
        }

        var plaintext = new byte[ciphertext.Length];
        var boundAad = BuildBoundAssociatedData(conversationId, envelope.CryptoEpoch, senderDeviceId, associatedData.Span);

        try
        {
            using var aes = new AesGcm(keyCopy, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, boundAad);

            lock (ctx.Gate)
            {
                ctx.SeenNonces.Add(replayKey);
            }

            return ValueTask.FromResult(plaintext);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Authenticated decryption failed; envelope may be tampered.", 400, ex);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Cryptographic verification failed.", 400, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    private void ConsumeAndValidateKeyPackage(DeviceKeyPackage pkg, DateTimeOffset now)
    {
        if (pkg.Consumed)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Device key package has already been consumed.", 409);
        if (pkg.ExpiresAt <= now)
            throw new MessagingDomainException(ErrorCodes.EnrollmentExpired, "Device key package has expired.", 410);
        if (!string.Equals(pkg.Protocol, CanonicalProtocolName, StringComparison.Ordinal))
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Device key package protocol is incompatible.", 400);
        if (!_consumedPackages.TryAdd(pkg.KeyPackageId, true))
            throw new MessagingDomainException(ErrorCodes.Conflict, "Device key package cannot be consumed more than once.", 409);
    }

    private static byte[] DeriveEphemeralSharedSecret(string recipientPublicKeySpkiBase64)
    {
        try
        {
            var peerBytes = Convert.FromBase64String(recipientPublicKeySpkiBase64);
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerBytes, out _);
            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            return ephemeral.DeriveKeyMaterial(peer.PublicKey);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            throw new MessagingDomainException(ErrorCodes.CryptoVerificationFailed, "Invalid P-256 public key package.", 400, ex);
        }
    }

    private static byte[] DeriveGroupEpochKey(ConversationId conversationId, long epoch, IReadOnlyList<byte[]> materialChunks)
    {
        var combined = new byte[materialChunks.Sum(c => c.Length)];
        var offset = 0;
        foreach (var chunk in materialChunks)
        {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        try
        {
            var salt = SHA256.HashData(Encoding.UTF8.GetBytes($"salt|{conversationId.Value}|{epoch.ToString(CultureInfo.InvariantCulture)}"));
            return DeriveEpochKeyDeterministic(combined, salt, conversationId, epoch);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    private static byte[] BuildBoundAssociatedData(
        ConversationId conversationId,
        long epoch,
        DeviceId senderDeviceId,
        ReadOnlySpan<byte> callerAad)
    {
        var prefix = Encoding.UTF8.GetBytes(
            $"{CanonicalProtocolName}|{conversationId.Value}|{epoch.ToString(CultureInfo.InvariantCulture)}|{senderDeviceId.Value}|");
        var result = new byte[prefix.Length + callerAad.Length];
        Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
        callerAad.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    private static void ZeroAll(IEnumerable<byte[]> buffers)
    {
        foreach (var buf in buffers)
            CryptographicOperations.ZeroMemory(buf);
    }

    private sealed record DeviceEpochWindow(long JoinedEpoch, long? RemovedEpoch);

    private sealed class GroupCryptoContext
    {
        public object Gate { get; } = new();
        public ConversationId ConversationId { get; }
        public long CurrentEpoch { get; private set; }
        public DateTimeOffset UpdatedAt { get; private set; }
        public Dictionary<long, byte[]> EpochKeys { get; } = new();
        public Dictionary<DeviceId, DeviceEpochWindow> MemberWindows { get; }
        public HashSet<string> SeenNonces { get; } = new(StringComparer.Ordinal);

        public GroupCryptoContext(
            ConversationId conversationId,
            long currentEpoch,
            byte[] initialKey,
            Dictionary<DeviceId, DeviceEpochWindow> memberWindows,
            DateTimeOffset updatedAt)
        {
            ConversationId = conversationId;
            CurrentEpoch = currentEpoch;
            EpochKeys[currentEpoch] = initialKey;
            MemberWindows = memberWindows;
            UpdatedAt = updatedAt;
        }

        public void AdvanceEpoch(long nextEpoch, byte[] nextKey, DateTimeOffset updatedAt)
        {
            CurrentEpoch = nextEpoch;
            EpochKeys[nextEpoch] = nextKey;
            UpdatedAt = updatedAt;
        }

        public bool IsDeviceAuthorizedForEpoch(DeviceId deviceId, long epoch)
        {
            if (!MemberWindows.TryGetValue(deviceId, out var window))
                return false;
            if (epoch < window.JoinedEpoch)
                return false;
            if (window.RemovedEpoch is not null && epoch >= window.RemovedEpoch.Value)
                return false;
            return true;
        }

        public MessagingCryptoGroupState ToState(string protocol)
        {
            var hash = Convert.ToHexString(SHA256.HashData(EpochKeys[CurrentEpoch])).ToLowerInvariant();
            var activeDevices = MemberWindows
                .Where(kv => kv.Value.RemovedEpoch is null)
                .Select(kv => kv.Key)
                .ToArray();
            return new MessagingCryptoGroupState(ConversationId, CurrentEpoch, protocol, hash, activeDevices, UpdatedAt);
        }
    }
}
