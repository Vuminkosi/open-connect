using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using Xunit;

namespace OpenConnect.Navigator.Operational.Tests.Durable;

public sealed class DurableOperationalStoreTests
{
    [Fact]
    public async Task ExpiredLeaseCanBeReacquiredWithHigherFenceAndOldLeaseIsRejected()
    {
        var database = new InMemoryDurableOperationalStore();
        var now = DateTimeOffset.UtcNow;
        var first = await database.TryAcquireLeaseAsync("execution", "worker-a", TimeSpan.FromSeconds(5), now, default);
        Assert.NotNull(first);
        Assert.Null(await database.TryAcquireLeaseAsync("execution", "worker-b", TimeSpan.FromSeconds(5), now, default));

        var second = await database.TryAcquireLeaseAsync("execution", "worker-b", TimeSpan.FromSeconds(5), now.AddSeconds(6), default);
        Assert.NotNull(second);
        Assert.True(second.FencingToken > first.FencingToken);
        Assert.False(await database.IsLeaseCurrentAsync(first, now.AddSeconds(6), default));
        Assert.True(await database.IsLeaseCurrentAsync(second, now.AddSeconds(6), default));
    }

    [Fact]
    public async Task RenewRequiresCurrentUnexpiredFence()
    {
        var database = new InMemoryDurableOperationalStore();
        var now = DateTimeOffset.UtcNow;
        var lease = await database.TryAcquireLeaseAsync("execution", "worker", TimeSpan.FromSeconds(5), now, default);
        Assert.NotNull(lease);
        Assert.NotNull(await database.RenewLeaseAsync(lease, TimeSpan.FromSeconds(5), now.AddSeconds(1), default));
        Assert.Null(await database.RenewLeaseAsync(lease, TimeSpan.FromSeconds(5), now.AddSeconds(6), default));
    }

    [Fact]
    public async Task CompletedReceiptIsScopedAndFirstWriterWins()
    {
        var store = new DurableIdempotencyStore(new InMemoryDurableOperationalStore());
        var receipt = new OperationalReceipt("exec", OperationalRunStatus.Completed, 2, "done", 9);
        Assert.True(await store.TryCompleteAsync("tenant-a", "key", receipt, DateTimeOffset.UtcNow));
        Assert.False(await store.TryCompleteAsync("tenant-a", "key", receipt with { ExecutionId = "other" }, DateTimeOffset.UtcNow));
        Assert.Equal(receipt, await store.GetAsync("tenant-a", "key"));
        Assert.Null(await store.GetAsync("tenant-b", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryCompleteAsync("tenant-a", "failed", receipt with { Status = OperationalRunStatus.Uncertain }, DateTimeOffset.UtcNow).AsTask());
    }

    [Fact]
    public void InMemoryStoreIsNeverReportedDurable()
    {
        var store = new InMemoryDurableOperationalStore();
        Assert.False(store.IsDurable);
        Assert.False(new DurableOperationalLeaseStore(store, "worker").IsDurable);
    }
}
