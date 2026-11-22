using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudAnalyticsClaimTests
{
    [Fact]
    public async Task AnalyticsClaimRequiresDeploymentAndWorkerPayloadProof()
    {
        var store = new RecordingStore();
        var service = new CloudExecutionClaimService(store);
        var id = Guid.NewGuid();
        var deployment = new CloudClassificationDeploymentMatchRequest("analytics", "region-a", "openconnect.v1",
            "revision-1", "sha256:" + new string('a', 64));
        async Task<CloudExecutionClaimResult> Claim(CloudClassificationDeploymentMatchRequest? selected, byte[]? proof) =>
            await service.ClaimAsync(id, id, id, id, "analytics.analyze", "active", new byte[32], selected,
                CancellationToken.None, proof);

        Assert.Equal("invalid_request", (await Claim(null, new byte[32])).Status);
        Assert.Equal("invalid_request", (await Claim(deployment, null)).Status);
        Assert.Equal("invalid_request", (await Claim(deployment, new byte[31])).Status);
        Assert.Equal(0, store.Calls);
        Assert.Equal("claimed", (await Claim(deployment, new byte[32])).Status);
        Assert.Equal(1, store.Calls);
        Assert.Equal("analytics.analyze", store.Capability);
    }

    private sealed class RecordingStore : ICloudExecutionClaimStore
    {
        public int Calls { get; private set; }
        public string? Capability { get; private set; }
        public Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId,
            Guid reservationId, string capabilityId, string fingerprintKeyId, byte[] fingerprint,
            CloudClassificationDeploymentMatchRequest? deployment, CancellationToken cancellationToken,
            byte[]? workerPayloadFingerprint = null, string? inferenceModelId = null,
            CloudInferenceDeploymentMatch? inferenceDeployment = null)
        {
            Calls++;
            Capability = capabilityId;
            return Task.FromResult(new CloudExecutionClaimResult("claimed", Guid.NewGuid()));
        }
    }
}
