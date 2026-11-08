namespace OpenConnect.Abstractions.Persistence;

/// <summary>An encrypted native agent session payload and its optimistic concurrency version.</summary>
/// <remarks>The payload must contain ciphertext only. This type makes a defensive copy on input and output.</remarks>
public sealed class NativeAgentSessionRecord
{
    private readonly byte[] _ciphertext;

    /// <summary>Creates an encrypted session record.</summary>
    /// <param name="binding">The complete identity and compatibility binding.</param>
    /// <param name="version">The positive persisted version.</param>
    /// <param name="ciphertext">Encrypted bytes; plaintext serialized state is not permitted.</param>
    /// <exception cref="ArgumentNullException">Binding or ciphertext is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Version is not positive.</exception>
    public NativeAgentSessionRecord(NativeAgentSessionBinding binding, long version, byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);

        Binding = binding;
        Version = version;
        _ciphertext = (byte[])ciphertext.Clone();
    }

    /// <summary>Gets the identity and compatibility binding.</summary>
    public NativeAgentSessionBinding Binding { get; }

    /// <summary>Gets the positive persisted version.</summary>
    public long Version { get; }

    /// <summary>Gets a copy of the encrypted payload.</summary>
    public byte[] GetCiphertextCopy() => (byte[])_ciphertext.Clone();
}
