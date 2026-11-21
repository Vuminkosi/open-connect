using OpenConnect.Privacy.Rules;

namespace OpenConnect.Docs.Cloud;

public static class CloudVoiceFeature
{
    public static IServiceCollection AddCloudVoiceFeature(this IServiceCollection services)
    {
        services.AddSingleton(new CloudVoicePrivacyService(new RulesSecurityDetector()));
        services.AddSingleton(new CloudVoiceOutputPrivacyService(new RulesSecurityDetector()));
        services.AddScoped<ICloudVoiceOutputEvidenceStore, EfCloudVoiceOutputEvidenceStore>();
        services.AddScoped<ICloudVoicePrivacyReader, EfCloudVoicePrivacyReader>();
        services.AddScoped<ICloudVoicePrivacyEvidenceStore, EfCloudVoicePrivacyEvidenceStore>();
        services.AddScoped<ICloudVoiceClaimStore, EfCloudVoiceClaimStore>();
        services.AddScoped<CloudVoiceClaimService>();
        services.AddScoped<ICloudVoiceAccessStore, EfCloudVoiceAccessStore>();
        services.AddScoped<ICloudVoiceConsumptionStore, EfCloudVoiceConsumptionStore>();
        services.AddScoped<CloudVoiceConsumptionService>();
        services.AddScoped<ICloudVoiceSettlementStore, EfCloudVoiceSettlementStore>();
        services.AddScoped<CloudVoiceSettlement>();
        return services;
    }

    public static void MapCloudVoiceGatewayEndpoints(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapCloudVoiceQuota(trust, databaseConfigured);
        app.MapCloudVoicePrivacy(trust, databaseConfigured);
        app.MapCloudVoiceOutputPrivacy(trust, databaseConfigured);
        app.MapCloudVoiceClaim(trust, databaseConfigured);
        app.MapCloudVoiceAccess(trust, databaseConfigured);
    }

    public static void MapCloudVoiceWorkerEndpoints(this WebApplication app, CloudWorkerClientTrust trust, bool databaseConfigured)
    {
        app.MapCloudVoiceConsumption(trust, databaseConfigured);
        app.MapCloudVoiceSettlement(trust, databaseConfigured);
    }
}
