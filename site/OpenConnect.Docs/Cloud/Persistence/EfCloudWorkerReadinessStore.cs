using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudWorkerReadinessStore(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudWorkerReadinessStore
{
    public static readonly IReadOnlyList<string> SupportedCapabilities =
    [
        "classification.classify",
        "inference.chat",
        "inference.liveui",
        "analytics.analyze",
        "knowledge.embed",
        "voice.synthesize",
        "translation.translate",
    ];

    public static bool IsSupportedCapability(string capabilityId) =>
        capabilityId is "classification.classify" or "inference.chat" or "inference.liveui" or
            "analytics.analyze" or "knowledge.embed" or "voice.synthesize" or "translation.translate";

    public async Task<bool> IsReadyAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken) &&
            !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any() &&
            await db.WorkerDeployments.AsNoTracking().AnyAsync(x => x.Id == deploymentId && x.Enabled &&
                (x.CapabilityId == "classification.classify" || x.CapabilityId == "inference.chat" || x.CapabilityId == "inference.liveui" ||
                 x.CapabilityId == "analytics.analyze" || x.CapabilityId == "knowledge.embed" || x.CapabilityId == "voice.synthesize" ||
                 x.CapabilityId == "translation.translate"), cancellationToken);
    }
}
