using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Tools;
using Xunit;

namespace OpenConnect.Tools.Tests;

/// <summary>
/// Verifies canonical tool descriptors (OC2-D-TOOLS-001), side-effect taxonomy and default deny (OC2-D-TOOLS-008),
/// output validation and size caps (OC2-D-TOOLS-007), retry classification (OC2-D-TOOLS-011),
/// and cancellation propagation (OC2-D-TOOLS-012).
/// </summary>
public sealed class CanonicalToolDescriptorTests
{
    private static readonly AgentId Agent = new("agent");
    private static readonly ToolId LookupToolId = new("lookup");
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly TaskId ToolTask = new(Ulid.NewUlid());
    private const string SimpleSchema = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""";

    [Fact]
    public void CanonicalToolDescriptor_And_Policy_Hold_Complete_Governance_Metadata()
    {
        var descriptor = new ToolDescriptor(LookupToolId, "CustomerLookup", Agent);
        Assert.Equal(LookupToolId, descriptor.Id);
        Assert.Equal("CustomerLookup", descriptor.Name);
        Assert.Equal(Agent, descriptor.AgentId);

        var policy = new ToolPolicy(
            LookupToolId,
            ToolImpact.ReadOnly,
            ToolIdempotency.SafeToRetry,
            ToolExecutionMode.Inline,
            ToolAcknowledgementMode.Automatic,
            ToolProgressMode.None,
            ToolApprovalMode.None);

        Assert.Equal(ToolImpact.ReadOnly, policy.Impact);
        Assert.Equal(ToolIdempotency.SafeToRetry, policy.Idempotency);
        Assert.Equal(ToolExecutionMode.Inline, policy.ExecutionMode);
        Assert.Equal(ToolAcknowledgementMode.Automatic, policy.AcknowledgementMode);
        Assert.Equal(ToolProgressMode.None, policy.ProgressMode);
        Assert.Equal(ToolApprovalMode.None, policy.ApprovalMode);

        var agentPolicy = new AgentToolPolicy(
            AllowedToolIds: [LookupToolId],
            AllowedScopes: ["orders.read"]);

        Assert.NotNull(agentPolicy.AllowedToolIds);
        Assert.Contains(LookupToolId, agentPolicy.AllowedToolIds);
        Assert.NotNull(agentPolicy.AllowedScopes);
        Assert.Contains("orders.read", agentPolicy.AllowedScopes);
    }

    [Theory]
    [InlineData(ToolImpact.ReadOnly, ToolApprovalMode.None, false)]
    [InlineData(ToolImpact.Write, ToolApprovalMode.PerInvocation, true)]
    [InlineData(ToolImpact.ExternalSideEffect, ToolApprovalMode.PerInvocationOnSideEffect, true)]
    public async Task SideEffectTaxonomy_EnforcesApprovalRequirements_AndConservativeUnknownToolDeny(
        ToolImpact impact, ToolApprovalMode approvalMode, bool requiresApproval)
    {
        Assert.True(approvalMode != ToolApprovalMode.None || !requiresApproval);
        var toolId = new ToolId("impact-" + impact.ToString().ToLowerInvariant());
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);
        var invoked = false;

        catalog.Register(new ToolRegistration(
            new ToolDescriptor(toolId, toolId.Value, Agent),
            SimpleSchema,
            (args, _) => { invoked = true; return ValueTask.FromResult(args.Clone()); },
            RequiresApproval: requiresApproval), [Agent]);

        var broker = new ToolBroker(catalog, auth);
        using var args = JsonDocument.Parse("""{"id":"order-123"}""");
        var request = new ToolAccessRequest(Agent, toolId, Principal, Tenant, ToolTask);

        if (requiresApproval)
        {
            // Unapproved invocation must throw approval_required and perform zero execution
            var ex = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(request, args.RootElement));
            Assert.Equal(ErrorCodes.ApprovalRequired, ex.ErrorCode);
            Assert.False(invoked);

            // Approved invocation succeeds
            var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(5));
            var result = await broker.InvokeAsync(request, args.RootElement, approval);
            Assert.True(invoked);
            Assert.Equal("order-123", result.GetProperty("id").GetString());
        }
        else
        {
            var result = await broker.InvokeAsync(request, args.RootElement);
            Assert.True(invoked);
            Assert.Equal("order-123", result.GetProperty("id").GetString());
        }

        // Conservative unknown tool default deny
        var unknownRequest = new ToolAccessRequest(Agent, new ToolId("unknown-tool"), Principal, Tenant, ToolTask);
        var unknownEx = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(unknownRequest, args.RootElement));
        Assert.Equal(ErrorCodes.ToolNotAllowed, unknownEx.ErrorCode);
    }

    [Fact]
    public async Task ToolOutputValidation_EnforcesSizeCap_AndRejectsExceedingPayloads()
    {
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);
        var largeString = new string('x', 2048);

        catalog.Register(new ToolRegistration(
            new ToolDescriptor(LookupToolId, "Lookup", Agent),
            SimpleSchema,
            (_, _) =>
            {
                using var doc = JsonDocument.Parse($$"""{"id":"{{largeString}}"}""");
                return ValueTask.FromResult(doc.RootElement.Clone());
            }), [Agent]);

        var broker = new ToolBroker(catalog, auth) { MaxOutputBytes = 1000 };
        using var args = JsonDocument.Parse("""{"id":"1"}""");
        var request = new ToolAccessRequest(Agent, LookupToolId, Principal, Tenant, ToolTask);

        var ex = await Assert.ThrowsAsync<ToolValidationException>(async () => await broker.InvokeAsync(request, args.RootElement));
        Assert.Contains("exceeds configured maximum", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Within limits succeeds
        broker.MaxOutputBytes = 10_000;
        var result = await broker.InvokeAsync(request, args.RootElement);
        Assert.Equal(largeString, result.GetProperty("id").GetString());
    }

    [Theory]
    // Non-idempotent operations MUST NEVER be retried, regardless of outcome
    [InlineData(ToolIdempotency.NonIdempotent, ToolExecutionState.Failed, false)]
    [InlineData(ToolIdempotency.NonIdempotent, ToolExecutionState.OutcomeUnknown, false)]
    [InlineData(ToolIdempotency.NonIdempotent, ToolExecutionState.Running, false)]
    // Uncertain outcome: only SafeToRetry may be replayed; IdempotencyKeyRequired requires verification before replay
    [InlineData(ToolIdempotency.IdempotencyKeyRequired, ToolExecutionState.OutcomeUnknown, false)]
    [InlineData(ToolIdempotency.IdempotencyKeyRequired, ToolExecutionState.Failed, true)]
    [InlineData(ToolIdempotency.SafeToRetry, ToolExecutionState.OutcomeUnknown, true)]
    [InlineData(ToolIdempotency.SafeToRetry, ToolExecutionState.Failed, true)]
    // Terminal success or non-failure states are not retriable
    [InlineData(ToolIdempotency.SafeToRetry, ToolExecutionState.Completed, false)]
    [InlineData(ToolIdempotency.SafeToRetry, ToolExecutionState.Cancelled, false)]
    public void RetryClassification_FollowsIdempotencyAndUncertaintyRules(
        ToolIdempotency idempotency, ToolExecutionState state, bool expectedCanRetry)
    {
        var canRetry = ToolRetryClassifier.CanRetry(idempotency, state);
        Assert.Equal(expectedCanRetry, canRetry);
    }

    [Fact]
    public async Task ToolCancellation_ThrowsPromptly_WithZeroSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);
        var invoked = false;

        catalog.Register(new ToolRegistration(
            new ToolDescriptor(LookupToolId, "Lookup", Agent),
            SimpleSchema,
            async (args, ct) =>
            {
                invoked = true;
                await System.Threading.Tasks.Task.Delay(5000, ct);
                return args.Clone();
            }), [Agent]);

        var broker = new ToolBroker(catalog, auth);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        using var args = JsonDocument.Parse("""{"id":"1"}""");
        var request = new ToolAccessRequest(Agent, LookupToolId, Principal, Tenant, ToolTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await broker.InvokeAsync(request, args.RootElement, cancellationToken: cts.Token));
        Assert.False(invoked);
    }

    private sealed class AllowAllPolicy : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }
}
