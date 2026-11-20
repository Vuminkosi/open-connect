namespace OpenConnect.Docs.Cloud;

public sealed record CloudInferenceModelPolicyView(IReadOnlyList<string> AllowedModels, int Revision);
public sealed record CloudInferenceModelPolicyResult(string Status, CloudInferenceModelPolicyView? Policy = null);

public interface ICloudInferenceModelPolicyStore
{
    Task<CloudInferenceModelPolicyResult> GetAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken);
    Task<CloudInferenceModelPolicyResult> PutAsync(CloudSubject actor, Guid tenantId, string[] models, int revision, CancellationToken cancellationToken);
    Task<bool> IsAllowedAsync(Guid tenantId, string modelId, CancellationToken cancellationToken);
    Task RecordRejectedAsync(CloudSubject actor, Guid tenantId, string outcome, CancellationToken cancellationToken);
}

public sealed class CloudInferenceModelPolicyService(ICloudInferenceModelPolicyStore store)
{
    public Task<CloudInferenceModelPolicyResult> GetAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty ? Task.FromResult(new CloudInferenceModelPolicyResult("not_found")) : store.GetAsync(actor, tenantId, cancellationToken);

    public Task RecordInvalidAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken) =>
        store.RecordRejectedAsync(actor, tenantId, "invalid_request", cancellationToken);

    public async Task<CloudInferenceModelPolicyResult> PutAsync(CloudSubject actor, Guid tenantId, string[]? models, int revision, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || revision < 0 || revision == int.MaxValue || models is null || models.Length > 64 ||
            models.Any(x => !ValidModelId(x)) || models.Distinct(StringComparer.Ordinal).Count() != models.Length)
        {
            await RecordInvalidAsync(actor, tenantId, cancellationToken);
            return new("invalid_request");
        }
        return await store.PutAsync(actor, tenantId, models.Order(StringComparer.Ordinal).ToArray(), revision, cancellationToken);
    }

    public Task<bool> IsAllowedAsync(Guid tenantId, string? modelId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty || !ValidModelId(modelId) ? Task.FromResult(false) : store.IsAllowedAsync(tenantId, modelId!, cancellationToken);

    public static bool ValidModelId(string? value) => value is { Length: > 0 and <= 256 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':');
}
