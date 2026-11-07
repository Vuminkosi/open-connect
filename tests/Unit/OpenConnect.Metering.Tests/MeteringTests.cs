using OpenConnect.Abstractions;
using OpenConnect.Metering;
using Xunit;

namespace OpenConnect.Metering.Tests;

public sealed class MeteringTests
{
    private static ModelUsageEvent Event(Ulid id, decimal input = 100, decimal? cost = 1m) =>
        new(id, IdGenerator(), "p", "m", new NormalizedUsage(new UsageQuantity(input, 10)),
            cost, cost is null ? CostState.Unknown : CostState.Known, UsageProvenance.ProviderReported);

    private static Ulid IdGenerator() => Ulid.NewUlid();

    [Fact]
    public async Task AppendIsIdempotentForIdenticalEventsAndRejectsConflicts()
    {
        var ledger = new InMemoryUsageLedger();
        var usage = Event(Ulid.NewUlid());

        Assert.True(await ledger.AppendAsync(usage));
        Assert.False(await ledger.AppendAsync(usage));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ledger.AppendAsync(usage with { Cost = 2m }));
    }

    [Fact]
    public void UnknownCostMustStayNullAndKnownCostMustHaveValue()
    {
        Assert.Throws<ArgumentException>(() => (Event(Ulid.NewUlid()) with { CostState = CostState.Unknown }).Validate());
        Assert.Throws<ArgumentException>(() => (Event(Ulid.NewUlid(), cost: null) with { CostState = CostState.Known }).Validate());
    }

    [Fact]
    public void PriceDoesNotDoubleCountCacheTokens()
    {
        var price = new ModelPrice(InputPerMillion: 1_000_000m, OutputPerMillion: 2_000_000m, CacheReadPerMillion: 100_000m);
        var usage = new NormalizedUsage(new UsageQuantity(Input: 100, Output: 10, CacheRead: 40));

        // 60 regular input *1 + 10 output *2 + 40 cache read *0.1 = 84
        Assert.Equal(84m, price.Calculate(usage));
    }

    [Fact]
    public void CacheUsageAboveInputIsRejected()
    {
        var usage = new NormalizedUsage(new UsageQuantity(10, 1, CacheRead: 11));
        Assert.Throws<ArgumentException>(() => usage.Validate());
    }

    [Fact]
    public void IncompleteUsageCannotBePriced() =>
        Assert.Throws<InvalidOperationException>(() =>
            new ModelPrice(1, 1).Calculate(new NormalizedUsage(new UsageQuantity(null, 5))));
}
