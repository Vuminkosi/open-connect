namespace OpenConnect.Docs.Cloud;

public sealed record CloudLiveUITokenUsage(long InputTokens, long OutputTokens);

public interface ICloudLiveUIDualSettlementStore
{
    Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken);
}

/// <summary>Validates a consumed LiveUI plan and its model token usage for settlement.</summary>
public sealed class CloudLiveUIDualSettlement(ICloudLiveUIDualSettlementStore store)
{
    public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || usage is null ||
        usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.InputTokens > 32768 || usage.OutputTokens > 32768 ||
        usage.InputTokens + usage.OutputTokens > 32768
            ? Task.FromResult("invalid_request") : store.SettleAsync(reservationId, claimId, deploymentId, usage, cancellationToken);
}
