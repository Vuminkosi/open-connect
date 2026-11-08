using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Agents.Execution;

/// <summary>Coordinates exclusive ownership of a native agent session while one turn is processed.</summary>
/// <remarks>
/// Implementations must verify that the binding's tenant, principal, and session correspond exactly to the
/// execution context, and must verify the caller's current authorized scope before granting ownership. A
/// <see cref="NativeAgentSessionBinding"/> is identity context, not proof of authorization. Providers must
/// serialize access across every host that can access the same session state. This contract does not define
/// lease renewal, and an implementation must not release ownership merely because cancellation was requested
/// while effects are still running. Host effect boundaries remain responsible for fencing or idempotency.
/// </remarks>
public interface INativeSessionTurnGate
{
    /// <summary>Attempts to acquire exclusive ownership for the complete load, run, and save lifecycle of a turn.</summary>
    /// <param name="binding">The exact native-session identity and compatibility binding.</param>
    /// <param name="executionContext">The host-only authority and resource context for this execution.</param>
    /// <param name="cancellationToken">Cancellation for this acquisition attempt; acquisition must not queue indefinitely.</param>
    /// <returns>An ownership handle, or <see langword="null"/> when acquisition is contended or rejected.</returns>
    /// <remarks>
    /// Null means the caller must not retry model execution; the caller may report contention or rejection.
    /// Acquisition failures propagate to the caller. The caller must validate the handle before protected work
    /// and dispose it only after all turn effects have stopped.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The acquisition attempt was canceled.</exception>
    Task<INativeSessionTurnOwnership?> TryAcquireAsync(
        NativeAgentSessionBinding binding,
        AgentExecutionContext executionContext,
        CancellationToken cancellationToken = default);
}
