using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudTranslationQuotaTests
{
    [Fact]
    public async Task RejectsInvalidScopeCharacterCountFingerprintAndDeadlineBeforeStore()
    {
        var store = new Store(); var service = new CloudTranslationQuota(store, TimeProvider.System);
        var request = new CloudQuotaRequest(Guid.NewGuid(), Guid.NewGuid(), "translation.translate", "character", Guid.NewGuid(),
            "active", new byte[32], 4, DateTimeOffset.UtcNow.AddMinutes(1));
        foreach (var invalid in new[] { request with { CapabilityId = "voice.synthesize" }, request with { Unit = "request" },
            request with { Amount = 0 }, request with { Amount = 5001 }, request with { RequestFingerprint = new byte[31] },
            request with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, request with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(6) } })
            Assert.Equal("invalid_request", (await service.ReserveAsync(invalid, default)).Status);
        Assert.Equal(0, store.Calls);
        Assert.Equal("reserved", (await service.ReserveAsync(request, default)).Status);
        Assert.Equal(1, store.Calls);
        Assert.NotSame(request.RequestFingerprint, store.Last!.RequestFingerprint);
    }

    private sealed class Store : ICloudTranslationQuotaStore
    {
        public int Calls { get; private set; }
        public CloudQuotaRequest? Last { get; private set; }
        public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++; Last = request;
            return Task.FromResult(new CloudQuotaResult("reserved", Guid.NewGuid()));
        }
    }
}
