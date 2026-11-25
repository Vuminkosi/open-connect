using OpenConnect.Abstractions;

namespace OpenConnect.Tools.Security;

/// <summary>
/// <see cref="IToolAccessPolicy"/> decorator that enforces dynamic security tool availability:
/// a security tool is discoverable and invocable ONLY when allowed by the base policy,
/// enabled by the host, approved by the application, enabled for the tenant, and backed by
/// a currently healthy (<see cref="SecurityCapabilityState.Ready"/>) unexpired capability lease.
/// </summary>
public sealed class SecurityToolAccessPolicy : IToolAccessPolicy
{
    private readonly IToolAccessPolicy _inner;
    private readonly SecurityToolPolicyOptions _options;
    private readonly ISecurityCapabilityHealthProvider _healthProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a new <see cref="SecurityToolAccessPolicy"/>.</summary>
    public SecurityToolAccessPolicy(
        IToolAccessPolicy inner,
        SecurityToolPolicyOptions options,
        ISecurityCapabilityHealthProvider healthProvider,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _healthProvider = healthProvider ?? throw new ArgumentNullException(nameof(healthProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await _inner.IsAllowedAsync(request, cancellationToken).ConfigureAwait(false))
            return false;

        if (!SecurityToolIds.IsSecurityTool(request.ToolId))
            return true;

        if (!_options.Enabled || !_options.ApplicationApprovedToolIds.Contains(request.ToolId.Value))
            return false;

        if (_options.IsCapabilityEnabledForTenant is null)
            return false;

        var requiredCapabilities = GetBackingCapabilities(request.ToolId.Value);
        if (requiredCapabilities.Count == 0)
            return false;

        // At least one backing capability must be tenant-enabled AND currently Ready in an unexpired snapshot.
        SecurityCapabilitiesSnapshot snapshot;
        try
        {
            snapshot = await _healthProvider.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (snapshot is null || snapshot.ExpiresAt <= _timeProvider.GetUtcNow() || snapshot.Capabilities is null)
            return false;

        foreach (var capabilityId in requiredCapabilities)
        {
            if (!_options.IsCapabilityEnabledForTenant(request.TenantId, capabilityId))
                continue;

            var status = snapshot.Capabilities.FirstOrDefault(
                c => string.Equals(c.Id, capabilityId, StringComparison.Ordinal));
            if (status is not null && status.State == SecurityCapabilityState.Ready)
                return true;
        }

        return false;
    }

    /// <summary>Returns the backing capability IDs for a canonical security tool.</summary>
    public static IReadOnlyList<string> GetBackingCapabilities(string toolId) => toolId switch
    {
        SecurityToolIds.ScanText => ["rules", "gliner-pii"],
        SecurityToolIds.ScanImage => ["paddle-ocr", "yunet"],
        SecurityToolIds.ScanFile => ["malware"],
        SecurityToolIds.ScanPrompt => ["deberta-injection"],
        SecurityToolIds.RedactContent => ["image-redaction", "rules", "paddle-ocr", "yunet"],
        SecurityToolIds.CheckDestination => ["service-fencing"],
        SecurityToolIds.CheckDataAccess => ["database-fencing"],
        SecurityToolIds.ExplainEvent => ["security-evidence"],
        _ => Array.Empty<string>()
    };
}
