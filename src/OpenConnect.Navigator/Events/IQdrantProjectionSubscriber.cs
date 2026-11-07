namespace OpenConnect.Navigator.Events;

/// <summary>Optional notification seam for downstream vector projection integrations.</summary>
public interface IQdrantProjectionSubscriber
{
    Task OnCommittedAsync(CommittedNavigatorEvent value, CancellationToken cancellationToken = default);
}
