using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Documents.Tests;

internal sealed class DisabledDecisionFirewall : IPrivacyFirewall
{
    public bool Enabled => true;
    public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "privacy_enforcement_disabled"));
}
