namespace OpenConnect.Abstractions.Persistence;

/// <summary>Result of an atomic native agent session save.</summary>
public enum NativeAgentSessionSaveOutcome
{
    /// <summary>The record was saved with its next version.</summary>
    Saved,

    /// <summary>The current persisted version did not equal the expected version.</summary>
    Conflict
}
