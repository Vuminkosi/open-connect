using System.Text.RegularExpressions;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudTranslationLanguagePair(string SourceLang, string TargetLang);
public sealed record CloudTranslationDeploymentProfile(Guid DeploymentId, long DeploymentRevision, long Revision,
    string TokenizerHash, IReadOnlyList<CloudTranslationLanguagePair> LanguagePairs);

public interface ICloudTranslationDeploymentProfileStore
{
    Task<CloudTranslationDeploymentProfile?> GetAsync(Guid deploymentId, CancellationToken cancellationToken);
    Task<string> PutAsync(CloudSubject actor, string traceId, CloudTranslationDeploymentProfile profile, long expectedRevision, CancellationToken cancellationToken);
    Task RecordAsync(CloudSubject actor, string traceId, Guid deploymentId, string outcome, bool denied, CancellationToken cancellationToken);
}

public sealed partial class CloudTranslationDeploymentProfiles(ICloudTranslationDeploymentProfileStore store)
{
    public Task RecordAsync(CloudSubject actor, string traceId, Guid deploymentId, string outcome, bool denied, CancellationToken cancellationToken) =>
        store.RecordAsync(actor, traceId, deploymentId, outcome, denied, cancellationToken);
    public Task<CloudTranslationDeploymentProfile?> GetAsync(Guid deploymentId, CancellationToken cancellationToken) =>
        deploymentId == Guid.Empty ? Task.FromResult<CloudTranslationDeploymentProfile?>(null) : store.GetAsync(deploymentId, cancellationToken);

    public Task<string> PutAsync(CloudSubject actor, string traceId, CloudTranslationDeploymentProfile profile, long expectedRevision, CancellationToken cancellationToken)
    {
        if (actor is null || string.IsNullOrWhiteSpace(actor.Issuer) || actor.Issuer.Length > 2048 ||
            string.IsNullOrWhiteSpace(actor.SubjectId) || actor.SubjectId.Length > 1024 || string.IsNullOrWhiteSpace(traceId) || traceId.Length > 128 ||
            profile is null || profile.DeploymentId == Guid.Empty || profile.DeploymentRevision <= 0 || expectedRevision < 0 || expectedRevision == long.MaxValue ||
            profile.TokenizerHash is null || !HashPattern().IsMatch(profile.TokenizerHash) || profile.LanguagePairs is null || profile.LanguagePairs.Count is < 1 or > 256 ||
            profile.LanguagePairs.Any(x => x is null || !ValidLanguage(x.SourceLang) || !ValidLanguage(x.TargetLang) || x.SourceLang == x.TargetLang) ||
            profile.LanguagePairs.Distinct().Count() != profile.LanguagePairs.Count)
            return Task.FromResult("invalid_request");
        return store.PutAsync(actor, traceId, profile with { LanguagePairs = profile.LanguagePairs.ToArray() }, expectedRevision, cancellationToken);
    }

    private static bool ValidLanguage(string? value) => value is not null && LanguagePattern().IsMatch(value);
    [GeneratedRegex("\\A[a-z]{3}_[A-Z][a-z]{3}\\z", RegexOptions.CultureInvariant)] private static partial Regex LanguagePattern();
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
}
