using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Tokenization;

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

/// <summary>Registers and configures the single host privacy firewall without duplicating instances.</summary>
public static class PrivacyServiceCollectionExtensions
{
    /// <summary>Ensures the single host privacy firewall, policy engine, egress gate, and encrypted token vault are registered.</summary>
    public static IServiceCollection EnsureHostPrivacyServices(
        this IServiceCollection services,
        Action<PrivacyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<PrivacyOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<ITokenVault>(_ => new TokenVault(RandomNumberGenerator.GetBytes(32)));
        services.TryAddSingleton<TokenGenerator>();
        services.TryAddSingleton<PolicyEngine>(sp => new PolicyEngine(sp.GetService<ISecurityDetector>()));
        services.TryAddSingleton<IPrivacyFirewall, PrivacyFirewall>();
        services.TryAddSingleton<IEgressGate, EgressGate>();
        return services;
    }
}
