namespace OpenConnect.Docs.Cloud;

public sealed record CloudInferenceTokenUsage(long InputTokens, long OutputTokens);

public interface ICloudInferenceTokenSettlementStore
{
    Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudInferenceTokenUsage usage, CancellationToken cancellationToken);
}

public sealed class CloudInferenceTokenSettlement(ICloudInferenceTokenSettlementStore store)
{
    public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudInferenceTokenUsage usage, CancellationToken cancellationToken) =>
        reservationId == Guid.Empty || claimId == Guid.Empty || deploymentId == Guid.Empty || usage.InputTokens < 0 || usage.OutputTokens < 0 ||
            usage.InputTokens > long.MaxValue - usage.OutputTokens
            ? Task.FromResult("invalid_request") : store.SettleAsync(reservationId, claimId, deploymentId, usage, cancellationToken);
}
