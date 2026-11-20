namespace OpenConnect.Docs.Cloud;

public sealed record CloudClassificationDeploymentMatchRequest(string? EndpointReference, string? Region, string? ContractVersion, string? ModelRevision);
public sealed record CloudClassificationDeploymentMatchResponse(bool Matched);

public interface ICloudClassificationDeploymentReader
{
    Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken);
}

public sealed class CloudClassificationDeploymentService(ICloudClassificationDeploymentReader reader)
{
    public Task<bool> MatchesAsync(Guid tenantId, CloudClassificationDeploymentMatchRequest request, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || !IsValid(request))
            return Task.FromResult(false);
        return reader.MatchesAsync(tenantId, request, cancellationToken);
    }

    public static bool IsValid(CloudClassificationDeploymentMatchRequest? request) => request is not null &&
        CloudDeploymentRegistry.ValidToken(request.EndpointReference, 128) && CloudDeploymentRegistry.ValidToken(request.Region, 80) &&
        CloudDeploymentRegistry.ValidToken(request.ContractVersion, 32) && !string.IsNullOrWhiteSpace(request.ModelRevision) && request.ModelRevision.Length <= 256;
}
