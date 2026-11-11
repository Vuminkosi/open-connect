using System.Collections.Immutable;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-009: Restrict child context by identity, tenant, classification,
/// purpose, data zone, model route and delegated token budget.
/// Every attribute is tested individually for fail-closed rejection.
/// </summary>
public sealed class ChildContextRestrictionTests
{
    private static readonly AgentId AuthorizedChild = new("child-analyzer");
    private static readonly AgentId UnauthorizedChild = new("child-attacker");

    private static AgentExecutionContext CreateParent() => new()
    {
        TenantScope = "tenant-prod-1",
        PrincipalOrWorkloadScope = "scope:financial-review",
        IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()),
        TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
        Depth = 0,
        CatalogRevision = 1,
        PolicyRevision = 1,
        AuthRevision = 1,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(30),
        RemainingBudgets = ImmutableDictionary<string, long>.Empty.Add("tokens", 5000),
        CancellationToken = CancellationToken.None
    };

    private static ChildContextRestrictionRequest ValidRequest() => new(
        ChildAgentId: AuthorizedChild,
        TenantScope: "tenant-prod-1",
        DataClass: "internal",
        PurposeScope: "scope:financial-review",
        DataZone: "us-east-zone-1",
        ModelRoute: "route-standard",
        DelegatedTokenBudget: 2000);

    [Fact]
    public void AllAttributesMatching_PassesValidation()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["public", "internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);

        ChildContextRestrictionGate.ValidateChildRestriction(
            parent,
            parentDataZone: "us-east-zone-1",
            parentModelRoute: "route-standard",
            allowedDataClasses: allowedClasses,
            allowedChildAgents: allowedChildren,
            request: ValidRequest());
    }

    [Fact]
    public void IdentityRestriction_UnauthorizedChild_ThrowsUnauthorizedAccessException()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { ChildAgentId = UnauthorizedChild };

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("not authorized for delegation", ex.Message);
    }

    [Fact]
    public void TenantRestriction_CrossTenantDelegation_ThrowsUnauthorizedAccessException()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { TenantScope = "tenant-attacker-9" };

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("Cross-tenant", ex.Message);
    }

    [Fact]
    public void ClassificationRestriction_UnauthorizedDataClass_ThrowsUnauthorizedAccessException()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["public"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { DataClass = "top-secret-pii" };

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("Data classification", ex.Message);
    }

    [Fact]
    public void PurposeRestriction_WidenedScope_ThrowsUnauthorizedAccessException()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { PurposeScope = "scope:admin-override" };

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("purpose/scope cannot widen", ex.Message);
    }

    [Fact]
    public void DataZoneRestriction_MismatchedZone_ThrowsUnauthorizedAccessException()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { DataZone = "eu-west-zone-2" };

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("Child data zone", ex.Message);
    }

    [Fact]
    public void ModelRouteRestriction_MismatchedRoute_ThrowsUnauthorizedAccessException()
    {
        var parent = CreateParent();
        var allowedClasses = new HashSet<string>(["internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { ModelRoute = "route-unrestricted-gpu" };

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("Child model route", ex.Message);
    }

    [Fact]
    public void TokenBudgetRestriction_ExceedingParentBudget_ThrowsInvalidOperationException()
    {
        var parent = CreateParent(); // Remaining tokens = 5000
        var allowedClasses = new HashSet<string>(["internal"], StringComparer.Ordinal);
        var allowedChildren = new HashSet<AgentId>([AuthorizedChild]);
        var req = ValidRequest() with { DelegatedTokenBudget = 99999 };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ChildContextRestrictionGate.ValidateChildRestriction(
                parent, "us-east-zone-1", "route-standard", allowedClasses, allowedChildren, req));
        Assert.Contains("exceeds parent remaining token budget", ex.Message);
    }
}
