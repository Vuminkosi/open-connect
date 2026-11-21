namespace OpenConnect.Docs.Cloud;

public sealed record CloudPrivacyCategoryCount(string Category, int Count);
public sealed record CloudPrivacyAssessment(CloudPrivacyDecision Decision, IReadOnlyList<CloudPrivacyCategoryCount> Categories);
