namespace OpenConnect.Abstractions;

/// <summary>A committed event together with the delivery acknowledgement operations.</summary>
/// <param name="Event">The committed event being delivered.</param>
/// <param name="AckAsync">Acknowledges successful processing of this delivery.</param>
/// <param name="NakAsync">Rejects this delivery, optionally providing a reason.</param>
/// <param name="Redelivered">Whether the event has previously been delivered.</param>
/// <remarks>
/// A delivery is not considered successfully processed until <see cref="AckAsync"/> is invoked.
/// Consumers should use the acknowledgement operations supplied by the transport adapter.
/// </remarks>
public sealed record CommittedEventDelivery(
    CommittedAgentEvent Event,
    Func<CancellationToken, ValueTask> AckAsync,
    Func<string?, CancellationToken, ValueTask> NakAsync,
    bool Redelivered);
