using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class WorkerEgressValidationTests
{
    [Fact]
    public void Rejects_unchanged_or_partially_retained_detected_text()
    {
        const string source = "😀 secret";
        var span = new EntitySpan(2, 6, "SECRET");
        Assert.False(WorkerEgressValidation.IsSafe(source, new(true, source, [span], SecurityPolicyAction.Allow, "test")));
        Assert.False(WorkerEgressValidation.IsSafe(source, new(true, "other secret", [span], SecurityPolicyAction.Mask, "test")));
        Assert.True(WorkerEgressValidation.IsSafe(source, new(true, "😀 [MASKED]", [span], SecurityPolicyAction.Mask, "test")));
    }

    [Fact]
    public void Rejects_invalid_offsets_and_missing_decisions()
    {
        Assert.False(WorkerEgressValidation.IsSafe("secret", null));
        Assert.False(WorkerEgressValidation.IsSafe("secret", new(true, "masked", [new(0, 100, "SECRET")], SecurityPolicyAction.Mask, "test")));
        Assert.False(WorkerEgressValidation.IsSafe("secret", new(true, "masked", [new(-1, 1, "SECRET")], SecurityPolicyAction.Mask, "test")));
    }

    [Fact]
    public void Rejects_review_or_unknown_actions_even_if_decision_claims_allow()
    {
        Assert.False(WorkerEgressValidation.IsSafe("safe", new(true, "safe", [], SecurityPolicyAction.Review, "test")));
        Assert.False(WorkerEgressValidation.IsSafe("safe", new(true, "safe", [], (SecurityPolicyAction)999, "test")));
    }
}
