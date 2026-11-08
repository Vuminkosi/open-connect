namespace OpenConnect.Abstractions.Persistence;

/// <summary>Stores encrypted native agent session state within an exact identity and compatibility scope.</summary>
public interface INativeAgentSessionStore
{
    /// <summary>Gets the record for the exact binding, or <see langword="null"/> when no matching record exists.</summary>
    /// <remarks>Implementations must not return a record with a different binding.</remarks>
    Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default);

    /// <summary>Atomically creates or updates a record using compare-and-swap version semantics.</summary>
    /// <param name="record">Encrypted record whose version is exactly <paramref name="expectedVersion"/> plus one.</param>
    /// <param name="expectedVersion">Zero when creating an absent record; otherwise the currently stored version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="NativeAgentSessionSaveOutcome.Saved"/> when persisted, otherwise <see cref="NativeAgentSessionSaveOutcome.Conflict"/>.</returns>
    /// <remarks>Save is atomic: absent rows may be created only with expected version zero; existing rows may be replaced only when their version equals expectedVersion. Successful saves persist expectedVersion + 1. Negative values, overflow, and a record version other than that exact next version must be rejected. Store encrypted payloads only, never raw serialized state or public transcripts.</remarks>
    Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default);
}
