namespace OpenConnect.Persistence.EFCore.Outbox;

public interface ICommittedOutboxPublisher
{
    Task PublishAsync(string subject, string eventId, string payload, CancellationToken ct = default);
}
