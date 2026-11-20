namespace OpenConnect.Docs.Cloud;

public interface ICloudLiveUISettlementStore
{
    Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken);
}

public sealed class CloudLiveUISettlement(ICloudLiveUISettlementStore store)
{
    public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty
            ? Task.FromResult("invalid_request") : store.SettleAsync(reservationId, claimId, deploymentId, cancellationToken);
}
