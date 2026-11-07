using OpenConnect.Metering;
using Xunit;

namespace OpenConnect.Metering.Tests;

public sealed class BudgetTests
{
    private static InMemoryBudgetGuard Guard(MissingCostPolicy policy = MissingCostPolicy.TreatAsReserved) =>
        new(new Dictionary<string, decimal> { ["t1"] = 10m }, policy);

    [Fact]
    public void ReservationsCountAgainstLimitAndFailClosed()
    {
        var guard = Guard();
        Assert.NotNull(guard.TryReserve("t1", 6m));
        Assert.Null(guard.TryReserve("t1", 5m));
        Assert.NotNull(guard.TryReserve("t1", 4m));
    }

    [Fact]
    public void UnknownScopeIsDenied() => Assert.Null(Guard().TryReserve("other", 0m));

    [Fact]
    public void SettleIsIdempotentAndFreesUnusedReservation()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 8m)!;
        guard.Settle(r, 3m);
        guard.Settle(r, 3m);

        Assert.Equal(3m, guard.Spent("t1"));
        Assert.NotNull(guard.TryReserve("t1", 7m));
    }

    [Fact]
    public void UnknownCostChargesReservationUnlessPolicyDenies()
    {
        var conservative = Guard();
        conservative.Settle(conservative.TryReserve("t1", 4m)!, null);
        Assert.Equal(4m, conservative.Spent("t1"));
    }

    [Fact]
    public void DenyPolicyBlocksScopeAfterUnknownCost()
    {
        var guard = Guard(MissingCostPolicy.Deny);
        guard.Settle(guard.TryReserve("t1", 1m)!, null);

        Assert.Equal(1m, guard.Spent("t1"));
        Assert.Null(guard.TryReserve("t1", 1m));
    }

    [Fact]
    public void ReleaseReturnsCapacityWithoutSpend()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 10m)!;
        guard.Release(r);
        guard.Release(r);

        Assert.Equal(0m, guard.Spent("t1"));
        Assert.NotNull(guard.TryReserve("t1", 10m));
    }

    [Fact]
    public async Task ConcurrentReservationsNeverExceedLimit()
    {
        var guard = Guard();
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => guard.TryReserve("t1", 1m))));
        Assert.Equal(10, results.Count(r => r is not null));
    }
}

