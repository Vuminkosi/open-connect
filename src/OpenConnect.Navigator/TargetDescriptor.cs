namespace OpenConnect.Navigator;

public sealed record NavigatorTargetDescriptor(
    string SchemaVersion,
    string TargetId,
    string ComponentId,
    int ComponentVersion,
    string Label,
    string Description,
    string RouteId,
    IReadOnlySet<NavigatorActionType> AllowedActions,
    IReadOnlySet<string> RequiredCapabilities,
    string Sensitivity,
    IReadOnlyList<NavigatorSuggestion>? Suggestions = null);

public sealed record NavigatorSuggestion(string Id, string Text);
public sealed record NavigatorTargetScope(string TenantId, string PrincipalId, string RouteId, IReadOnlySet<string> Capabilities);
public sealed record NavigatorRegisteredTarget(NavigatorTargetDescriptor Descriptor, string TenantId, string PrincipalId, long Revision);

public sealed class NavigatorTargetRegistry
{
    public const string CurrentSchemaVersion = "openconnect.navigator.target/v1";
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Principal, string Route, string Id), NavigatorRegisteredTarget> _targets = new();

    public void Register(NavigatorTargetDescriptor descriptor, string tenantId, string principalId, long revision)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(descriptor.TargetId) || string.IsNullOrWhiteSpace(descriptor.RouteId) || revision < 1)
            throw new ArgumentException("Target descriptor is invalid.", nameof(descriptor));
        if (descriptor.AllowedActions.Any(action => !Enum.IsDefined(action)))
            throw new ArgumentException("Target descriptor contains an unsupported action.", nameof(descriptor));
        var target = new NavigatorRegisteredTarget(descriptor, tenantId, principalId, revision);
        lock (_gate) _targets[(tenantId, principalId, descriptor.RouteId, descriptor.TargetId)] = target;
    }

    public IReadOnlyList<NavigatorRegisteredTarget> Resolve(string targetId, NavigatorTargetScope scope)
    {
        lock (_gate)
            return _targets.Values.Where(t => t.Descriptor.TargetId == targetId && t.TenantId == scope.TenantId && t.PrincipalId == scope.PrincipalId &&
                t.Descriptor.RouteId == scope.RouteId && t.Descriptor.RequiredCapabilities.All(scope.Capabilities.Contains)).ToArray();
    }

    /// <summary>Lists targets visible on a route to the supplied tenant, principal, and capabilities.</summary>
    public IReadOnlyList<NavigatorRegisteredTarget> ListByRoute(string routeId, NavigatorTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(routeId) || routeId != scope.RouteId) return Array.Empty<NavigatorRegisteredTarget>();
        lock (_gate)
            return _targets.Values.Where(t => t.TenantId == scope.TenantId && t.PrincipalId == scope.PrincipalId &&
                t.Descriptor.RouteId == routeId && t.Descriptor.RequiredCapabilities.All(scope.Capabilities.Contains)).ToArray();
    }

    /// <summary>Validates grant expiry, identity bindings, and the fixed-time digest of normalized JSON arguments.</summary>
    public bool TryValidate(CommandGrant grant, string normalizedArgsJson, string expectedTenant,
        string expectedPrincipal, string sessionId, long policyRevision, DateTimeOffset utcNow, out string reason)
    {
        reason = "invalid_grant";
        if (grant is null || string.IsNullOrWhiteSpace(normalizedArgsJson)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(normalizedArgsJson);
            if (!grant.IsValidFor(sessionId, expectedTenant, expectedPrincipal, grant.ClientInstance,
                    grant.CommandId, grant.Sequence, grant.Action, grant.TargetId, grant.TargetRevision,
                    policyRevision, document.RootElement, utcNow)) return false;
            lock (_gate)
            {
                var registered = _targets.Values.Where(t => t.TenantId == expectedTenant && t.PrincipalId == expectedPrincipal &&
                    t.Descriptor.TargetId == grant.TargetId && t.Revision == grant.TargetRevision &&
                    t.Descriptor.AllowedActions.Contains(grant.Action)).Take(2).Count();
                if (registered != 1) return false;
            }
            reason = string.Empty;
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }


    public bool IsCurrent(string targetId, long revision, NavigatorTargetScope scope) =>
        Resolve(targetId, scope) is [{ Revision: var current }] && current == revision;
}
