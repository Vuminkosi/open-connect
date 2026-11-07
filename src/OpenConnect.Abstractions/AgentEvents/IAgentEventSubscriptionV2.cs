namespace OpenConnect.Abstractions;

/// <summary>Subscribes consumers to committed agent events with explicit delivery acknowledgement.</summary>
/// <remarks>
/// Unlike <see cref="IAgentEventSubscription"/>, advancing this stream does not acknowledge a delivery.
/// Consumers must explicitly acknowledge or reject each delivery. Transport adapters and durable
/// projections that require reliable processing should use this contract; the v1 contract remains
/// available for simple consumers that do not require acknowledgement semantics.
/// </remarks>
public interface IAgentEventSubscriptionV2
{
    /// <summary>Streams committed events with explicit acknowledgement operations.</summary>
    /// <param name="options">Named consumer and event type filter.</param>
    /// <param name="cancellationToken">Token used to stop the subscription.</param>
    /// <returns>An asynchronous stream of acknowledged deliveries.</returns>
    IAsyncEnumerable<CommittedEventDelivery> SubscribeAsync(
        AgentEventSubscriptionOptions options,
        CancellationToken cancellationToken = default);
}
