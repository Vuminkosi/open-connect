using System.Collections.Immutable;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ContextProjectionTests
{
    private static readonly AgentId Child = new("child");

    [Fact]
    public void Build_NoneWithItemsThrows()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Builder().Build(Parent(), AgentContextSharingMode.None,
            ["internal"], [Item("safe")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Build_NoneEmptyOk()
    {
        var (projection, quarantined) = Builder().Build(Parent(), AgentContextSharingMode.None,
            ["internal"], [], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        Assert.Empty(projection.Items);
        Assert.Equal(0, quarantined);
    }

    [Fact]
    public void Build_CancelledParentThrowsBeforeReturningProjection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var parent = Parent() with { CancellationToken = cancellation.Token };
        AgentContextProjection? projection = null;

        Assert.Throws<OperationCanceledException>(() =>
        {
            var result = Builder().Build(parent, AgentContextSharingMode.TaskSummary,
                ["internal"], [Item("secret") with { ContainsSecret = true }], Child, [], ["principal"],
                100, TimeSpan.FromMinutes(5));
            projection = result.Projection;
        });

        Assert.Null(projection);
    }

    [Fact]
    public void RequestAdditional_NoneDenied()
    {
        var (projection, _) = Builder().Build(Parent(), AgentContextSharingMode.None,
            ["internal"], [], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(projection, Child, []));
    }

    [Fact]
    public void Build_QuarantinesSecretAndToolControlItems()
    {
        var (projection, quarantined) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("safe"), Item("secret") with { ContainsSecret = true }, Item("tool") with { ToolControl = true }],
            Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        Assert.Single(projection.Items);
        Assert.Equal(2, quarantined);
    }

    [Fact]
    public void Build_DeniesWiderChildScope()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("safe")], Child, [], ["other"], 100, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Build_EnforcesTokenBudget()
    {
        Assert.Throws<InvalidOperationException>(() => Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("large") with { TokenEstimate = 101 }], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Build_RejectsNegativeTokenEstimateWithoutReturningProjection()
    {
        AgentContextProjection? projection = null;

        Assert.Throws<ArgumentException>(() =>
        {
            var result = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
                ["internal"], [Item("negative") with { TokenEstimate = -1 }], Child, [], ["principal"],
                100, TimeSpan.FromMinutes(5));
            projection = result.Projection;
        });

        Assert.Null(projection);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void Build_RejectsInvalidBudgetOrTtlWithoutReturningProjection(int tokenBudget, int ttlMinutes)
    {
        AgentContextProjection? projection = null;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var result = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
                ["internal"], [Item("safe")], Child, [], ["principal"], tokenBudget,
                TimeSpan.FromMinutes(ttlMinutes));
            projection = result.Projection;
        });

        Assert.Null(projection);
    }

    [Theory]
    [InlineData("", "safe summary")]
    [InlineData("   ", "safe summary")]
    [InlineData("safe", null)]
    [InlineData("safe", "")]
    [InlineData("safe", "   ")]
    public void Build_RejectsMalformedItemsWithoutReturningProjection(string id, string? summary)
    {
        AgentContextProjection? projection = null;

        Assert.Throws<ArgumentException>(() =>
        {
            var result = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
                ["internal"], [Item(id) with { SummaryOrRef = summary! }], Child, [], ["principal"],
                100, TimeSpan.FromMinutes(5));
            projection = result.Projection;
        });

        Assert.Null(projection);
    }

    [Fact]
    public void RequestAdditional_AppendsImmutableRevision()
    {
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("first")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));
        var (next, _) = Builder().RequestAdditional(initial, Child, [Item("second")]);

        Assert.Equal(1, initial.Version);
        Assert.Equal(2, next.Version);
        Assert.Single(initial.Items);
        Assert.Equal(2, next.Items.Length);
    }

    [Fact]
    public void RequestAdditional_RevisionOverflowThrowsAndLeavesOriginalUnchanged()
    {
        var (built, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("first")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));
        var initial = built with { Version = int.MaxValue };
        var originalVersion = initial.Version;
        var originalItems = initial.Items;
        var originalBudget = initial.TokenBudget;
        AgentContextProjection? next = null;

        Assert.Throws<OverflowException>(() =>
        {
            var result = Builder().RequestAdditional(initial, Child, [Item("second")]);
            next = result.Projection;
        });

        Assert.Null(next);
        Assert.Equal(originalVersion, initial.Version);
        Assert.Equal(originalItems, initial.Items);
        Assert.Equal(originalBudget, initial.TokenBudget);
    }

    [Fact]
    public void Build_PreservesItemsWithDuplicateIdsInInputOrder()
    {
        var first = Item("duplicate") with { SummaryOrRef = "first summary" };
        var second = Item("duplicate") with { SummaryOrRef = "second summary" };

        var (projection, quarantined) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [first, second], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        Assert.Equal(0, quarantined);
        Assert.Equal(["first summary", "second summary"], projection.Items.Select(item => item.SummaryOrRef));
    }

    [Fact]
    public void RequestAdditional_PreservesDuplicateIdsAcrossExistingAndAdditionalItems()
    {
        var originalItem = Item("duplicate") with { SummaryOrRef = "original" };
        var additionalItem = Item("duplicate") with { SummaryOrRef = "additional" };
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [originalItem], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        var (next, quarantined) = Builder().RequestAdditional(initial, Child, [additionalItem]);

        Assert.Equal(0, quarantined);
        Assert.Equal("original", Assert.Single(initial.Items).SummaryOrRef);
        Assert.Equal(["original", "additional"], next.Items.Select(item => item.SummaryOrRef));
        Assert.Equal(1, initial.Version);
        Assert.Equal(2, next.Version);
    }

    [Fact]
    public void RequestAdditional_RejectsCombinedBudgetAndLeavesOriginalUnchanged()
    {
        const int budget = 10;
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("first") with { TokenEstimate = 6 }], Child, [], ["principal"],
            budget, TimeSpan.FromMinutes(5));
        var originalVersion = initial.Version;
        var originalItems = initial.Items;
        var originalBudget = initial.TokenBudget;
        var extra = Item("second") with { TokenEstimate = 6 };

        Assert.True(extra.TokenEstimate <= budget);
        Assert.True(initial.Items.Sum(item => item.TokenEstimate) <= budget);
        Assert.Throws<InvalidOperationException>(() =>
            Builder().RequestAdditional(initial, Child, [extra]));

        Assert.Equal(originalVersion, initial.Version);
        Assert.Equal(originalItems, initial.Items);
        Assert.Equal(originalBudget, initial.TokenBudget);
    }

    [Fact]
    public void RequestAdditional_RejectsMalformedExtraItemsAndLeavesOriginalUnchanged()
    {
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("first")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));
        var originalVersion = initial.Version;
        var originalItems = initial.Items;
        var originalBudget = initial.TokenBudget;

        Assert.Throws<ArgumentException>(() => Builder().RequestAdditional(initial, Child,
            [Item("extra") with { SummaryOrRef = "   " }]));

        Assert.Equal(originalVersion, initial.Version);
        Assert.Equal(originalItems, initial.Items);
        Assert.Equal(originalBudget, initial.TokenBudget);
    }

    [Fact]
    public void RequestAdditional_RejectsNegativeTokenEstimateAndLeavesOriginalUnchanged()
    {
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("first")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));
        var originalVersion = initial.Version;
        var originalItems = initial.Items;
        var originalBudget = initial.TokenBudget;
        AgentContextProjection? next = null;

        Assert.Throws<ArgumentException>(() =>
        {
            var result = Builder().RequestAdditional(initial, Child,
                [Item("negative") with { TokenEstimate = -1 }]);
            next = result.Projection;
        });

        Assert.Null(next);
        Assert.Equal(originalVersion, initial.Version);
        Assert.Equal(originalItems, initial.Items);
        Assert.Equal(originalBudget, initial.TokenBudget);
    }

    [Fact]
    public void RequestAdditional_RejectsMismatchedChildIdentityAndLeavesOriginalUnchanged()
    {
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("first")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));
        var originalVersion = initial.Version;
        var originalItems = initial.Items;
        var originalBudget = initial.TokenBudget;
        var otherChild = new AgentId("other-child");

        Assert.Throws<UnauthorizedAccessException>(() =>
            Builder().RequestAdditional(initial, otherChild, [Item("second")]));

        Assert.Equal(originalVersion, initial.Version);
        Assert.Equal(originalItems, initial.Items);
        Assert.Equal(originalBudget, initial.TokenBudget);
    }

    [Fact]
    public void RequestAdditional_AllowsAnyClassInProjectionAllowlist()
    {
        var (initial, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal", "public"], [Item("first")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        var (next, quarantined) = Builder().RequestAdditional(initial, Child,
            [Item("public-item") with { DataClass = "public" }]);

        Assert.Equal("internal,public", initial.DataClass);
        Assert.Equal(2, next.Version);
        Assert.Contains(next.Items, item => item.Id == "public-item");
        Assert.Equal(0, quarantined);
        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(initial, Child,
            [Item("restricted-item") with { DataClass = "restricted" }]));
    }

    [Fact]
    public void RequestAdditional_CommaClassIsAuthorizedAsOneClass()
    {
        var (projection, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["a,b"], [Item("initial") with { DataClass = "a,b" }], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        var (next, _) = Builder().RequestAdditional(projection, Child, [Item("comma") with { DataClass = "a,b" }]);

        Assert.Equal("a\\,b", projection.DataClass);
        Assert.Contains(next.Items, item => item.Id == "comma");
        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(projection, Child,
            [Item("a") with { DataClass = "a" }]));
        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(projection, Child,
            [Item("b") with { DataClass = "b" }]));
    }

    [Fact]
    public void Build_CommaSetAndCommaClassHaveDistinctAuthorizationKeys()
    {
        var (twoClasses, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["a", "b"], [], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));
        var (oneClass, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["a,b"], [], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        Assert.NotEqual(twoClasses.DataClass, oneClass.DataClass);
        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(twoClasses, Child,
            [Item("combined") with { DataClass = "a,b" }]));
        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(oneClass, Child,
            [Item("a") with { DataClass = "a" }]));
        Assert.Throws<UnauthorizedAccessException>(() => Builder().RequestAdditional(oneClass, Child,
            [Item("b") with { DataClass = "b" }]));
    }

    [Fact]
    public void RequestAdditional_BackslashClassRoundTrips()
    {
        var (projection, _) = Builder().Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["a\\b"], [], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        var (next, _) = Builder().RequestAdditional(projection, Child, [Item("slash") with { DataClass = "a\\b" }]);

        Assert.Equal("a\\\\b", projection.DataClass);
        Assert.Contains(next.Items, item => item.Id == "slash");
    }

    [Fact]
    public void Build_SetsExpiryFromInjectedClock()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var builder = new ContextProjectionBuilder(new FixedTimeProvider(now));

        var (projection, _) = builder.Build(Parent(), AgentContextSharingMode.TaskSummary,
            ["internal"], [Item("safe")], Child, [], ["principal"], 100, TimeSpan.FromMinutes(5));

        Assert.Equal(now.AddMinutes(5), projection.ExpiresAt);
    }

    [Fact]
    public void RequestAdditional_DeniesExtensionAtProjectionExpiry()
    {
        var created = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var (projection, _) = new ContextProjectionBuilder(new FixedTimeProvider(created)).Build(
            Parent(), AgentContextSharingMode.TaskSummary, ["internal"], [Item("safe")], Child, [],
            ["principal"], 100, TimeSpan.FromMinutes(5));
        var atExpiry = new ContextProjectionBuilder(new FixedTimeProvider(projection.ExpiresAt));

        Assert.Throws<UnauthorizedAccessException>(() =>
            atExpiry.RequestAdditional(projection, Child, [Item("extra")]));
    }

    private static ContextProjectionBuilder Builder() => new(new FixedTimeProvider(DateTimeOffset.UtcNow));
    private static ContextItem Item(string id) => new(id, "internal", "safe summary", "source", 1);
    private static AgentExecutionContext Parent() => new()
    {
        TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1, PolicyRevision = 2,
        AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddHours(1), RemainingBudgets = ImmutableDictionary<string, long>.Empty,
        CancellationToken = CancellationToken.None
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
