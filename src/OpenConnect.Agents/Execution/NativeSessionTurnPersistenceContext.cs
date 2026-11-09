using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Agents.Execution;

/// <summary>Host-provided durable session dependencies for one native conversation turn.</summary>
/// <remarks>The binding is identity context, not authorization. Hosts must authenticate and authorize each invocation, provide a protected persistence implementation, and supply a gate that coordinates every host sharing the session. The gate alone does not fence effects already in flight.</remarks>
public sealed class NativeSessionTurnPersistenceContext
{
    /// <summary>Creates the required persistence, identity binding, and exclusive turn-gate context.</summary>
    /// <param name="persistence">The native-session persistence adapter; it must use host-approved protected storage.</param>
    /// <param name="binding">The exact session and compatibility identity selected by the authenticated host; this binding is not proof of authorization.</param>
    /// <param name="turnGate">The host-provided gate responsible for exclusive ownership; the interface itself provides no cross-host topology guarantee.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public NativeSessionTurnPersistenceContext(NativeAgentSessionPersistence persistence,
        NativeAgentSessionBinding binding, INativeSessionTurnGate turnGate)
    {
        Persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        TurnGate = turnGate ?? throw new ArgumentNullException(nameof(turnGate));
    }

    /// <summary>Gets the native-session persistence adapter.</summary>
    public NativeAgentSessionPersistence Persistence { get; }

    /// <summary>Gets the exact host-selected session identity and compatibility binding.</summary>
    /// <remarks>The binding supplies identity context only; the host remains responsible for authentication and current authorization.</remarks>
    public NativeAgentSessionBinding Binding { get; }

    /// <summary>Gets the host-provided exclusive turn gate.</summary>
    /// <remarks>Cross-host coordination depends on the implementation and is not guaranteed by this context type.</remarks>
    public INativeSessionTurnGate TurnGate { get; }
}
