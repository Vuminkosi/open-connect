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

    [Fact]
    public void SettleUsesStoredAmountWhenCallerInflatesAmount()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 4m)!;

        guard.Settle(r with { Amount = r.Amount * 10 }, null);

        // Charged the stored 4, not the forged 40; 6 remains reservable and 7 does not.
        Assert.Equal(4m, guard.Spent("t1"));
        Assert.Null(guard.TryReserve("t1", 7m));
        Assert.NotNull(guard.TryReserve("t1", 6m));
    }

    [Fact]
    public void ReleaseUsesStoredAmountWhenCallerInflatesAmount()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 4m)!;
        var other = guard.TryReserve("t1", 5m)!;

        guard.Release(r with { Amount = 100m });

        Assert.Equal(0m, guard.Spent("t1"));
        // Reserved is 5 (other), not negative: 5 more fits, 6 does not.
        Assert.Null(guard.TryReserve("t1", 6m));
        Assert.NotNull(guard.TryReserve("t1", 5m));
        guard.Release(other);
    }

    [Fact]
    public void UnknownCallerScopeDoesNotThrowOrStrandReservation()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 10m)!;

        guard.Settle(r with { ScopeKey = "forged" }, 2m);

        Assert.Equal(2m, guard.Spent("t1"));
        Assert.Equal(0m, guard.Spent("forged"));
        Assert.NotNull(guard.TryReserve("t1", 8m));

        var r2 = guard.TryReserve("t1", 0m)!;
        guard.Release(r2 with { ScopeKey = "forged" });
        Assert.Equal(2m, guard.Spent("t1"));
    }

    [Fact]
    public void UnknownCallerScopeReleaseFreesStoredScope()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 10m)!;

        guard.Release(r with { ScopeKey = "forged" });

        Assert.NotNull(guard.TryReserve("t1", 10m));
    }

    [Fact]
    public void RepeatedSettleAndReleaseWithForgedValuesAreNoOps()
    {
        var guard = Guard();
        var r = guard.TryReserve("t1", 4m)!;
        guard.Settle(r, 3m);

        guard.Settle(r with { Amount = 9m }, 3m);
        guard.Release(r with { ScopeKey = "forged" });
        guard.Release(r);

        Assert.Equal(3m, guard.Spent("t1"));
        Assert.NotNull(guard.TryReserve("t1", 7m));
    }

    [Fact]
    public void NeverIssuedIdIsNoOp()
    {
        var guard = Guard();
        var ghost = new BudgetReservation(Guid.NewGuid(), "t1", 5m);

        guard.Settle(ghost, 5m);
        guard.Release(ghost);

        Assert.Equal(0m, guard.Spent("t1"));
        Assert.NotNull(guard.TryReserve("t1", 10m));
    }

    [Fact]
    public void DenyPolicyWithForgedScopeBlocksOnlyStoredScope()
    {
        var guard = new InMemoryBudgetGuard(
            new Dictionary<string, decimal> { ["t1"] = 10m, ["t2"] = 10m }, MissingCostPolicy.Deny);
        var r = guard.TryReserve("t1", 1m)!;

        guard.Settle(r with { ScopeKey = "t2" }, null);

        Assert.Null(guard.TryReserve("t1", 1m));
        Assert.NotNull(guard.TryReserve("t2", 1m));
    }
}
