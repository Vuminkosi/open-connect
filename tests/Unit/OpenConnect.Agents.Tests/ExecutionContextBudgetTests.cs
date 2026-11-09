using System.Collections.Immutable;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ExecutionContextBudgetTests
{
    [Fact]
    public void Request_capped_at_parent_remaining()
    {
        using var parent = CreateContext();

        using var child = parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty.Add("A", 100), CancellationToken.None);

        Assert.Equal(10, child.RemainingBudgets["A"]);
        Assert.Equal(10, parent.RemainingBudgets["A"]);
        Assert.Equal(5, parent.RemainingBudgets["B"]);
    }

    [Fact]
    public void Unknown_budget_key_rejected_without_changing_parent()
    {
        using var parent = CreateContext();
        var originalBudgets = parent.RemainingBudgets;

        Assert.Throws<InvalidOperationException>(() => parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty.Add("Nope", 1), CancellationToken.None));

        Assert.Same(originalBudgets, parent.RemainingBudgets);
        Assert.Equal(10, parent.RemainingBudgets["A"]);
        Assert.Equal(5, parent.RemainingBudgets["B"]);
    }

    [Fact]
    public void Negative_budget_request_rejected_without_changing_parent()
    {
        using var parent = CreateContext();
        var originalBudgets = parent.RemainingBudgets;

        Assert.Throws<InvalidOperationException>(() => parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty.Add("A", -1), CancellationToken.None));

        Assert.Same(originalBudgets, parent.RemainingBudgets);
        Assert.Equal(10, parent.RemainingBudgets["A"]);
        Assert.Equal(5, parent.RemainingBudgets["B"]);
    }

    [Fact]
    public void Parent_record_immutable_after_child_creation()
    {
        using var parent = CreateContext();
        var originalBudgets = parent.RemainingBudgets;
        var originalValues = originalBudgets.ToImmutableDictionary();

        using var child = parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty.Add("A", 4), CancellationToken.None);

        Assert.Same(originalBudgets, parent.RemainingBudgets);
        Assert.Equal(originalValues, parent.RemainingBudgets);
        Assert.Equal(4, child.RemainingBudgets["A"]);
        Assert.DoesNotContain("B", child.RemainingBudgets);
    }

    [Fact]
    public void Zero_or_empty_request_yields_exact_requested_intersection()
    {
        using var parent = CreateContext();

        using var zeroChild = parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty.Add("A", 0), CancellationToken.None);
        using var emptyChild = parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty, CancellationToken.None);

        Assert.Equal(0, zeroChild.RemainingBudgets["A"]);
        Assert.Empty(emptyChild.RemainingBudgets);
    }

    private static AgentExecutionContext CreateContext() => new()
    {
        TenantScope = "tenant",
        PrincipalOrWorkloadScope = "principal",
        IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()),
        TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
        Depth = 0,
        CatalogRevision = 1,
        PolicyRevision = 1,
        AuthRevision = 1,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        RemainingBudgets = ImmutableDictionary<string, long>.Empty.Add("A", 10).Add("B", 5),
        CancellationToken = CancellationToken.None
    };
}
