namespace OpenConnect.Docs.Cloud;

public sealed record CloudLiveUITokenUsage(long InputTokens, long OutputTokens);

public interface ICloudLiveUIDualSettlementStore
{
    Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken);
    Task<string> FailAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken);
    Task<string> ObserveAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken);
    Task<string> RecoverAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken);
}

/// <summary>Validates a consumed LiveUI plan and its model token usage for settlement.</summary>
public sealed class CloudLiveUIDualSettlement(ICloudLiveUIDualSettlementStore store)
{
    public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        ApplyAsync(reservationId, claimId, deploymentId, usage, false, cancellationToken);

    public Task<string> FailAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        ApplyAsync(reservationId, claimId, deploymentId, usage, true, cancellationToken);

    public Task<string> ObserveAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        !Valid(reservationId, claimId, deploymentId, usage) ? Task.FromResult("invalid_request")
            : store.ObserveAsync(reservationId, claimId, deploymentId, usage, cancellationToken);

    public Task<string> RecoverAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        !Valid(reservationId, claimId, deploymentId, usage) ? Task.FromResult("invalid_request")
            : store.RecoverAsync(reservationId, claimId, deploymentId, usage, cancellationToken);

    private Task<string> ApplyAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, bool failed, CancellationToken cancellationToken) =>
        !Valid(reservationId, claimId, deploymentId, usage)
            ? Task.FromResult("invalid_request") : failed
                ? store.FailAsync(reservationId, claimId, deploymentId, usage, cancellationToken)
                : store.SettleAsync(reservationId, claimId, deploymentId, usage, cancellationToken);

    private static bool Valid(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage) =>
        reservationId != Guid.Empty && claimId != Guid.Empty && deploymentId != Guid.Empty && usage is not null &&
        usage.InputTokens is >= 0 and <= 32768 && usage.OutputTokens is >= 0 and <= 32768 && usage.InputTokens + usage.OutputTokens <= 32768;
}
