using System.Text.RegularExpressions;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudTranslationDeploymentMatchRequest(Guid DeploymentId, string EndpointReference, string Region,
    string ContractVersion, string ModelId, string ModelRevision, string ModelDigest, string ImageDigest,
    string TokenizerHash, string SourceLang, string TargetLang);

public interface ICloudTranslationDeploymentReader
{
    Task<bool> MatchesAsync(Guid tenantId, CloudTranslationDeploymentMatchRequest request, CancellationToken cancellationToken);
}

public sealed partial class CloudTranslationDeploymentMatch(ICloudTranslationDeploymentReader reader)
{
    public Task<bool> MatchesAsync(Guid tenantId, CloudTranslationDeploymentMatchRequest? request, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || request is null || request.DeploymentId == Guid.Empty ||
            !CloudDeploymentRegistry.ValidToken(request.EndpointReference, 128) || !CloudDeploymentRegistry.ValidToken(request.Region, 80) ||
            !CloudDeploymentRegistry.ValidToken(request.ContractVersion, 32) || !CloudInferenceModelPolicyService.ValidModelId(request.ModelId) ||
            string.IsNullOrWhiteSpace(request.ModelRevision) || request.ModelRevision.Length > 256 || !Digest(request.ModelDigest) || !Digest(request.ImageDigest) ||
            request.TokenizerHash is null || !HashPattern().IsMatch(request.TokenizerHash) || !Language(request.SourceLang) || !Language(request.TargetLang) ||
            request.SourceLang == request.TargetLang)
            return Task.FromResult(false);
        return reader.MatchesAsync(tenantId, request, cancellationToken);
    }

    private static bool Digest(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) && HashPattern().IsMatch(value[7..]);
    private static bool Language(string? value) => value is not null && LanguagePattern().IsMatch(value);
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("\\A[a-z]{3}_[A-Z][a-z]{3}\\z", RegexOptions.CultureInvariant)] private static partial Regex LanguagePattern();
}
