using OpenConnect.Privacy.Rules;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationFeature
{
    public static IServiceCollection AddCloudTranslationFeature(this IServiceCollection services)
    {
        services.AddSingleton(new CloudTranslationPrivacyService(new RulesSecurityDetector()));
        services.AddScoped<ICloudTranslationPrivacyEvidenceStore, EfCloudTranslationPrivacyEvidenceStore>();
        return services;
    }
}
