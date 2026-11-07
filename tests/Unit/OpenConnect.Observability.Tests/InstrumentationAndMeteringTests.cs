using OpenConnect.Metering;
using OpenConnect.Observability;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Observability.Tests;

public sealed class InstrumentationAndMeteringTests
{
    [Fact]
    public void Tags_only_allow_bounded_safe_dimensions()
    {
        var tags = Instrumentation.AllowlistedTags(new[] {
            new KeyValuePair<string, object?>("operation", "chat"),
            new KeyValuePair<string, object?>("prompt", "CANARY PII person@example.com"),
            new KeyValuePair<string, object?>("provider", "Bearer secret-token"),
            new KeyValuePair<string, object?>("model", "gpt-4") });
        Assert.Single(tags);
        Assert.Equal("chat", tags[0].Value);
    }

    [Fact]
    public void Cache_read_is_subset_not_double_counted()
    {
        var usage = new NormalizedUsage(new UsageQuantity(1_000, 500, 200));
        Assert.Equal(0.0218m, new ModelPrice(1, 2, 100).Calculate(usage));
    }

    [Fact]
    public void Unknown_cost_is_preserved_as_null()
    {
        var evt = Event(null, CostState.Unknown);
        Assert.Null(evt.Validate().Cost);
    }

    [Fact]
    public void Known_cost_requires_amount()
    {
        Assert.Throws<ArgumentException>(() => Event(null, CostState.Known).Validate());
    }

    [Fact]
    public void Incomplete_usage_cannot_be_priced_as_zero()
    {
        Assert.Throws<InvalidOperationException>(() => new ModelPrice(1, 1).Calculate(new(new(null, 10))));
    }

    [Fact]
    public void Usage_validation_rejects_negative_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageQuantity(-1, 0).Validate());
    }

    [Fact]
    public async Task Ledger_append_is_idempotent_and_rejects_conflicting_reuse()
    {
        var ledger = new InMemoryUsageLedger();
        var item = Event(0.2m, CostState.Known);
        Assert.True(await ledger.AppendAsync(item));
        Assert.False(await ledger.AppendAsync(item));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ledger.AppendAsync(item with { Cost = 0.3m }));
    }

    private static ModelUsageEvent Event(decimal? cost, CostState state) => new(Ulid.NewUlid(), Ulid.NewUlid(),
        "provider", "model", new(new(1, 1)), cost, state, UsageProvenance.ProviderReported);
}
