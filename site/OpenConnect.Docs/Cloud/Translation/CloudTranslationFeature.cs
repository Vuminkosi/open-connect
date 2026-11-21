using OpenConnect.Privacy.Rules;

namespace OpenConnect.Docs.Cloud;

public static class CloudTranslationFeature
{
    public static IServiceCollection AddCloudTranslationFeature(this IServiceCollection services)
    {
        services.AddSingleton(new CloudTranslationPrivacyService(new RulesSecurityDetector()));
        services.AddScoped<ICloudTranslationPrivacyEvidenceStore, EfCloudTranslationPrivacyEvidenceStore>();
        services.AddScoped<ICloudTranslationQuotaStore, EfCloudTranslationQuotaStore>();
        services.AddScoped<CloudTranslationQuota>();
        services.AddScoped<ICloudTranslationDeploymentProfileStore, EfCloudTranslationDeploymentProfileStore>();
        services.AddScoped<CloudTranslationDeploymentProfiles>();
        services.AddScoped<ICloudTranslationDeploymentReader, EfCloudTranslationDeploymentReader>();
        services.AddScoped<CloudTranslationDeploymentMatch>();
        return services;
    }
}
