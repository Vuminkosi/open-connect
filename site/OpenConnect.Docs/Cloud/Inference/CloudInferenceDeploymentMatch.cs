namespace OpenConnect.Docs.Cloud;

public sealed record CloudInferenceDeploymentMatch(string? EndpointReference, string? Region, string? ContractVersion, string? ModelRevision, string? ModelDigest)
{
    public bool IsValid() => CloudDeploymentRegistry.ValidToken(EndpointReference, 128) && CloudDeploymentRegistry.ValidToken(Region, 80) &&
        CloudDeploymentRegistry.ValidToken(ContractVersion, 32) && !string.IsNullOrWhiteSpace(ModelRevision) && ModelRevision.Length <= 256 &&
        ModelDigest is not null && System.Text.RegularExpressions.Regex.IsMatch(ModelDigest, "^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
