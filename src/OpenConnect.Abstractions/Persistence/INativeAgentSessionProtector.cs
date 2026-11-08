using System.Security.Cryptography;

namespace OpenConnect.Abstractions.Persistence;

/// <summary>Protects and restores native session bytes using authenticated encryption bound to a session identity.</summary>
/// <remarks>No default or plaintext implementation is provided. Host implementations are responsible for authenticated encryption, key management, and verifying the supplied binding during unprotection. Read-only memory does not guarantee immutable backing storage. The caller retains ownership of input memory, which must remain valid and unmodified until the returned task completes; implementations must not retain the input memory after completion. Ownership of the returned byte array transfers to the caller. Callers must clear plaintext buffers they own after use.</remarks>
public interface INativeAgentSessionProtector
{
    /// <summary>Protects serialized native session bytes for the supplied binding. The input contains plaintext only for the duration of this call.</summary>
    /// <param name="binding">The exact identity and compatibility scope to authenticate.</param>
    /// <param name="plaintext">The serialized bytes to protect; caller-owned and unchanged until the returned task completes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ciphertext byte array whose ownership transfers to the caller.</returns>
    /// <exception cref="ArgumentNullException">Binding is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);

    /// <summary>Unprotects ciphertext only when it authenticates for the supplied binding.</summary>
    /// <param name="binding">The exact identity and compatibility scope to verify.</param>
    /// <param name="ciphertext">Ciphertext; caller-owned and unchanged until the returned task completes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A plaintext byte array whose ownership transfers to the caller; caller must clear it after use.</returns>
    /// <exception cref="ArgumentNullException">Binding is null.</exception>
    /// <exception cref="CryptographicException">Ciphertext is invalid or does not authenticate for the supplied binding.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default);
}
