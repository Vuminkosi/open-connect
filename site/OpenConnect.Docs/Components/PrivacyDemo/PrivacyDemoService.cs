using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;

namespace OpenConnect.Docs.Components.PrivacyDemo;

public sealed record PrivacyDemoResult(string Input, EgressDecision Decision);

/// <summary>Evaluates fixed synthetic examples with the SDK's local rules firewall.</summary>
public sealed class PrivacyDemoService
{
    private static readonly IReadOnlyDictionary<string, string> Examples = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["email"] = "Contact alex@example.test about the order.",
        ["ordinary"] = "The order is ready for collection."
    };

    // No vault is supplied: the demo uses non-reversible placeholders and retains no token mappings.
    private readonly IPrivacyFirewall _firewall = new PrivacyFirewall(
        Options.Create(new PrivacyOptions()), new PolicyEngine(new RulesSecurityDetector()));

    public async ValueTask<PrivacyDemoResult> EvaluateAsync(string example, CancellationToken cancellationToken = default)
    {
        if (!Examples.TryGetValue(example, out var input))
            throw new ArgumentException("Unknown privacy example.", nameof(example));
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        var decision = await _firewall.EvaluateAsync(input, context, cancellationToken);
        return new PrivacyDemoResult(input, decision);
    }
}
