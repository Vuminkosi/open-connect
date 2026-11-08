using System.Text.Json;
using Microsoft.Agents.AI;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Agents.Execution;

/// <summary>Persists MAF-native session state as protected bytes under an exact host-supplied binding.</summary>
/// <remarks>
/// This host-internal adapter does not authenticate or authorize the binding. Callers must authenticate and
/// re-authorize the principal, and select the correct agent, provider, and compatibility version before use.
/// It persists native session state only; it does not persist a duplicate public transcript or history.
/// </remarks>
public sealed class NativeAgentSessionPersistence
{
    private readonly INativeAgentSessionStore _store;
    private readonly INativeAgentSessionProtector _protector;

    /// <summary>Creates the adapter with the required encrypted session store and protector.</summary>
    /// <param name="store">Store that enforces exact-binding lookup and atomic versioned saves.</param>
    /// <param name="protector">Host-provided protector for native session bytes; no default protector is supplied.</param>
    /// <exception cref="ArgumentNullException">Either required dependency is <see langword="null"/>.</exception>
    public NativeAgentSessionPersistence(INativeAgentSessionStore store, INativeAgentSessionProtector protector)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        _store = store;
        _protector = protector;
    }

    /// <summary>Loads and restores the session for the exact supplied binding, or returns null when absent.</summary>
    public async Task<(AgentSession Session, long Version)?> LoadAsync(
        AIAgent agent, NativeAgentSessionBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ValidateBinding(binding);
        ct.ThrowIfCancellationRequested();

        var record = await _store.GetAsync(binding, ct).ConfigureAwait(false);
        if (record is null)
            return null;

        if (record.Binding != binding || record.Version <= 0)
            throw new InvalidDataException("The native session store returned a record outside the requested scope.");

        var ciphertext = record.GetCiphertextCopy();
        byte[]? plaintext = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            plaintext = await _protector.UnprotectAsync(binding, ciphertext, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(plaintext);
            var session = await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: ct).ConfigureAwait(false);
            return (session, record.Version);
        }
        finally
        {
            Array.Clear(ciphertext);
            if (plaintext is not null)
                Array.Clear(plaintext);
        }
    }

    /// <summary>Serializes, protects, then atomically saves a native session using compare-and-swap versioning.</summary>
    public async Task<NativeAgentSessionSaveOutcome> SaveAsync(
        AIAgent agent,
        AgentSession session,
        NativeAgentSessionBinding binding,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        ValidateBinding(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        var nextVersion = checked(expectedVersion + 1);
        ct.ThrowIfCancellationRequested();

        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(serialized);
            ct.ThrowIfCancellationRequested();
            ciphertext = await _protector.ProtectAsync(binding, plaintext, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var record = new NativeAgentSessionRecord(binding, nextVersion, ciphertext);
            return await _store.SaveAsync(record, expectedVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            if (plaintext is not null)
                Array.Clear(plaintext);
            if (ciphertext is not null)
                Array.Clear(ciphertext);
        }
    }

    private static void ValidateBinding(NativeAgentSessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.DefinitionRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.MafVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.ProviderId);
    }
}
