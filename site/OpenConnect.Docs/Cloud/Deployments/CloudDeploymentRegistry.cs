using System.Text.RegularExpressions;

namespace OpenConnect.Docs.Cloud;

public interface ICloudDeploymentStore
{
    Task<string> PutAsync(CloudSubject actor, string traceId, CloudWorkerDeployment deployment, long expectedRevision, CancellationToken cancellationToken);
    Task RecordAsync(CloudSubject actor, string traceId, Guid deploymentId, string outcome, CancellationToken cancellationToken);
    Task RecordDeniedAsync(CloudSubject actor, string traceId, Guid deploymentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudWorkerDeployment>> ListAsync(string capabilityId, int offset, int limit, CancellationToken cancellationToken);
}

public sealed class CloudDeploymentRegistry(ICloudDeploymentStore store)
{
    public Task RecordDeniedAsync(CloudSubject actor, string traceId, Guid deploymentId, CancellationToken cancellationToken) =>
        store.RecordDeniedAsync(actor, traceId, deploymentId, cancellationToken);

    public Task RecordInvalidAsync(CloudSubject actor, string traceId, Guid deploymentId, CancellationToken cancellationToken) =>
        store.RecordAsync(actor, traceId, deploymentId, "invalid_request", cancellationToken);
    public Task<IReadOnlyList<CloudWorkerDeployment>> ListAsync(string capabilityId, int offset, int limit, CancellationToken cancellationToken) =>
        store.ListAsync(capabilityId, offset, limit, cancellationToken);

    public async Task<string> PutAsync(CloudSubject actor, string traceId, CloudWorkerDeployment deployment, long expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(deployment);
        if (string.IsNullOrWhiteSpace(actor.Issuer) || actor.Issuer.Length > 2048 || string.IsNullOrWhiteSpace(actor.SubjectId) || actor.SubjectId.Length > 1024 ||
            string.IsNullOrWhiteSpace(traceId) || traceId.Length > 128) throw new ArgumentException("A valid actor and trace identifier are required.");
        if (deployment.Id == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue ||
            !ValidToken(deployment.CapabilityId, 80) || !ValidToken(deployment.EndpointReference, 128) ||
            deployment.Transport != "https-mtls" || !ValidToken(deployment.Region, 80) ||
            !ValidToken(deployment.ContractVersion, 32) || !ValidModel(deployment) ||
            !Digest(deployment.ImageDigest) || deployment.ConcurrencyLimit is < 1 or > 100000)
        {
            await store.RecordAsync(actor, traceId, deployment.Id, "invalid_request", cancellationToken);
            return "invalid_request";
        }
        return await store.PutAsync(actor, traceId, deployment, expectedRevision, cancellationToken);
    }

    public static bool ValidToken(string? value, int maxLength) => value is not null && value.Length is > 0 && value.Length <= maxLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool Digest(string? value) => value is not null && Regex.IsMatch(value, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static bool ValidModel(CloudWorkerDeployment deployment) =>
        deployment.ModelId is null && deployment.ModelRevision is null && deployment.ModelDigest is null ||
        !string.IsNullOrWhiteSpace(deployment.ModelId) && deployment.ModelId.Length <= 256 &&
        !string.IsNullOrWhiteSpace(deployment.ModelRevision) && deployment.ModelRevision.Length <= 256 && Digest(deployment.ModelDigest);
}
