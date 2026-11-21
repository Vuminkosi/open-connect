namespace OpenConnect.Docs.Cloud;

public interface ICloudVoiceSettlementStore
{
    Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken);
}

public sealed class CloudVoiceSettlement(ICloudVoiceSettlementStore store)
{
    public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty
            ? Task.FromResult("invalid_request") : store.SettleAsync(reservationId, claimId, deploymentId, cancellationToken);
}
