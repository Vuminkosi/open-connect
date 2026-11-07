using OpenConnect.Navigator.DurableStore;
using OpenConnect.Navigator.Operational.Durable;
using Xunit;

namespace OpenConnect.Navigator.DurableStore.Tests;

public sealed class InMemoryDurableNavigatorStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public async Task SessionsAreIsolatedByTenantAndPrincipal()
    {
        var store = CreateStore();
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        Assert.NotNull(await store.GetSessionAsync(id, "tenant", "principal"));
        Assert.Null(await store.GetSessionAsync(id, "other", "principal"));
        Assert.Null(await store.GetSessionAsync(id, "tenant", "other"));
    }

    [Fact]
    public async Task ConcurrentConsumeHasSingleWinnerAndIdenticalRetryReplays()
    {
        var store = CreateStore();
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        var approval = new NavigatorApprovalRecord(id, "command", "digest", Now.AddMinutes(1));
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.CreatePendingApprovalAsync(id, "tenant", "principal", approval));
        var receipt = new NavigatorReceiptRecord("command", "key", "receipt-digest", "saved", Now.AddMinutes(2), 5);

        var candidates = Enumerable.Range(0, 20).Select(index => receipt with { Result = $"result-{index}" }).ToArray();
        var outcomes = await Task.WhenAll(candidates.Select(candidate => Task.Run(async () =>
            (Candidate: candidate, Outcome: await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now, candidate)))));

        var winner = Assert.Single(outcomes, result => result.Outcome.Outcome == NavigatorStoreOutcome.Succeeded);
        Assert.False(winner.Outcome.IsReplay);
        Assert.Equal(19, outcomes.Count(result => result.Outcome.Outcome == NavigatorStoreOutcome.Conflict));
        Assert.Equal(winner.Candidate, await store.GetReceiptAsync("tenant", "principal", "key", Now));
        var replay = await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now, winner.Candidate);
        Assert.Equal(NavigatorStoreOutcome.Succeeded, replay.Outcome);
        Assert.True(replay.IsReplay);
        Assert.Equal(await store.GetReceiptAsync("tenant", "principal", "key", Now), winner.Candidate);
        Assert.Equal(NavigatorStoreOutcome.Conflict,
            (await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now, receipt with { Result = "different" })).Outcome);
    }

    [Fact]
    public async Task ExpiredApprovalIsDenied()
    {
        var store = CreateStore();
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        await store.CreatePendingApprovalAsync(id, "tenant", "principal", new NavigatorApprovalRecord(id, "command", "digest", Now));
        var result = await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now,
            new NavigatorReceiptRecord("command", "key", "receipt", null, Now.AddMinutes(1), 1));
        Assert.Equal(NavigatorStoreOutcome.Expired, result.Outcome);
    }

    [Fact]
    public async Task MatchingRetryAfterReceiptExpiryIsExpiredNotReplay()
    {
        var store = CreateStore();
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        await store.CreatePendingApprovalAsync(id, "tenant", "principal", new NavigatorApprovalRecord(id, "command", "digest", Now.AddMinutes(1)));
        var receipt = new NavigatorReceiptRecord("command", "key", "receipt", "saved", Now.AddMinutes(1), 1);
        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            (await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now,
                receipt with { ExpiresAt = Now.AddMinutes(2) })).Outcome);

        var retry = await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now.AddMinutes(2),
            receipt with { ExpiresAt = Now.AddMinutes(2) });

        Assert.Equal(NavigatorStoreOutcome.Expired, retry.Outcome);
        Assert.False(retry.IsReplay);
        Assert.Null(await store.GetReceiptAsync("tenant", "principal", "key", Now.AddMinutes(2)));
    }

    [Fact]
    public async Task DifferingRetryAfterReceiptExpiryIsExpiredNotConflict()
    {
        var store = CreateStore();
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        await store.CreatePendingApprovalAsync(id, "tenant", "principal", new NavigatorApprovalRecord(id, "command", "digest", Now.AddMinutes(1)));
        var receipt = new NavigatorReceiptRecord("command", "key", "receipt", "saved", Now.AddMinutes(1), 1);
        await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now,
            receipt with { ExpiresAt = Now.AddMinutes(2) });

        var retry = await store.ConsumeApprovalAndSaveReceiptAsync(id, "tenant", "principal", "command", "digest", Now.AddMinutes(2),
            receipt with { Digest = "different", Result = "different", ExpiresAt = Now.AddMinutes(2) });

        Assert.Equal(NavigatorStoreOutcome.Expired, retry.Outcome);
        Assert.False(retry.IsReplay);
    }

    [Fact]
    public async Task CancellationPropagatesAndOlderFenceIsRejected()
    {
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateSessionAsync("t", "p", "c", "r", cancellation.Token).AsTask());
        Assert.True(await store.IsFenceCurrentAsync("execution", 8, Now));
        Assert.False(await store.IsFenceCurrentAsync("execution", 7, Now));
        Assert.True(await store.IsFenceCurrentAsync("another", 1, Now));
    }

    private static InMemoryDurableNavigatorStore CreateStore() => new(new InMemoryDurableOperationalStore());
}
