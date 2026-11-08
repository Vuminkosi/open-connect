namespace OpenConnect.Agents.Execution;

/// <summary>Represents the caller's exclusive ownership of one native-session turn.</summary>
/// <remarks>
/// The handle covers the complete load, run, and save lifecycle. Ownership loss requests cooperative
/// cancellation; it does not roll back effects already performed. Callers must observe <see cref="OwnershipLost"/>,
/// validate ownership at protected boundaries, stop further effects after loss, and await disposal only after
/// in-flight effects have stopped. The gate must not consider ownership released solely because cancellation
/// was requested while work continues. This contract does not imply ownership renewal. Distributed effect
/// boundaries still require fencing or idempotency where needed.
/// </remarks>
public interface INativeSessionTurnOwnership : IAsyncDisposable
{
    /// <summary>Gets the token canceled when exclusive ownership is lost or expires.</summary>
    CancellationToken OwnershipLost { get; }

    /// <summary>Verifies that this handle still represents valid exclusive ownership.</summary>
    /// <param name="cancellationToken">Cancellation for the validation operation.</param>
    /// <returns>A task that completes when ownership is confirmed.</returns>
    /// <exception cref="OperationCanceledException">Validation was canceled or ownership loss was signaled.</exception>
    /// <exception cref="InvalidOperationException">Ownership is lost or expired; implementations fail closed.</exception>
    /// <remarks>Call before protected effects and at appropriate continuing-work boundaries.</remarks>
    Task ValidateOwnershipAsync(CancellationToken cancellationToken = default);

}
