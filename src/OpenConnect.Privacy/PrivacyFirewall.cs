using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Privacy;

/// <summary>Applies configured privacy options when evaluating content for egress.</summary>
public sealed class PrivacyFirewall : IPrivacyFirewall
{
    private readonly PrivacyOptions _options;
    private readonly PolicyEngine _policyEngine;

    /// <summary>Creates a firewall using the configured options and policy engine.</summary>
    /// <param name="options">Privacy configuration.</param>
    /// <param name="policyEngine">Engine used to evaluate content.</param>
    /// <exception cref="ArgumentOutOfRangeException">The configured default token lifetime is not positive.</exception>
    public PrivacyFirewall(IOptions<PrivacyOptions> options, PolicyEngine policyEngine)
    {
        _options = options.Value;
        _policyEngine = policyEngine;
        if (_options.DefaultTokenTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Token TTL must be positive.");
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled && _options.Mode != FirewallMode.Disabled;

    /// <inheritdoc />
    public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
        _policyEngine.EvaluateAsync(content, context, _options.Mode, _options.Enabled, cancellationToken);
}
