namespace OpenConnect.Privacy.Gates;

/// <summary>Central gate used by model, tool, worker, output, and transport boundaries.</summary>
public sealed class EgressGate(IPrivacyFirewall firewall) : IEgressGate
{
    /// <inheritdoc />
    public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
        firewall.EvaluateAsync(content, context, cancellationToken);

    /// <summary>Returns sanitized content if the privacy decision allows it.</summary>
    /// <param name="content">Content to evaluate.</param>
    /// <param name="context">Tenant, principal, session, and destination context.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The sanitized content.</returns>
    /// <exception cref="PrivacyBlockedException">The decision blocks content or has no sanitized content.</exception>
    public async ValueTask<string> DemandAllowedAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
    {
        var decision = await EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed || decision.SanitizedContent is null)
            throw new PrivacyBlockedException("Content was blocked by privacy policy.");
        return decision.SanitizedContent;
    }
}
