namespace OpenConnect.Abstractions;

/// <summary>Subscribes consumers to committed agent events delivered from the transactional outbox.</summary>
/// <remarks>This is a delivery contract; <see cref="IEventJournal"/> remains a journal, not a bus.</remarks>
public interface IAgentEventSubscription
{
    /// <summary>Streams committed events matching the supplied subscription options.</summary>
    /// <param name="options">Named consumer and event type filter.</param>
    /// <param name="cancellationToken">Token used to stop the subscription.</param>
    /// <returns>An asynchronous stream of committed events.</returns>
    IAsyncEnumerable<CommittedAgentEvent> SubscribeAsync(
        AgentEventSubscriptionOptions options,
        CancellationToken cancellationToken = default);
}
