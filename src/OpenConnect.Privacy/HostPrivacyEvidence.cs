using System.Collections.ObjectModel;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy;

/// <summary>Content-free metadata for one host privacy decision; this does not deliver an event.</summary>
public sealed class HostPrivacyEvidence
{
    private HostPrivacyEvidence(Guid eventId, string tenantId, EgressDestination destination,
        bool allowed, SecurityPolicyAction action, string reasonCode, IReadOnlyDictionary<string, int> categories)
    {
        EventId = eventId;
        TenantId = tenantId;
        Destination = destination;
        Allowed = allowed;
        Action = action;
        ReasonCode = reasonCode;
        Categories = categories;
    }

    /// <summary>Gets the caller-supplied stable event identifier for retry deduplication.</summary>
    public Guid EventId { get; }
    /// <summary>Gets the tenant identifier asserted by the local host.</summary>
    public string TenantId { get; }
    /// <summary>Gets the inspected boundary, including output where applicable.</summary>
    public EgressDestination Destination { get; }
    /// <summary>Gets whether the decision allowed content to proceed.</summary>
    public bool Allowed { get; }
    /// <summary>Gets the policy action applied by the firewall.</summary>
    public SecurityPolicyAction Action { get; }
    /// <summary>Gets an allowlisted reason category; raw detector messages are never included.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets counts of allowlisted categories; unknown categories are aggregated as OTHER.</summary>
    public IReadOnlyDictionary<string, int> Categories { get; }

    /// <summary>Creates bounded metadata without copying content, token values, entity offsets, or detector reasons.</summary>
    public static HostPrivacyEvidence Create(Guid eventId, EgressContext context, EgressDecision decision)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        if (eventId == Guid.Empty || context.Tenant.Value == default || context.Principal.Value == default ||
            context.Session.Value == default || !Enum.IsDefined(context.Destination) ||
            !Enum.IsDefined(decision.Action) || decision.Allowed && decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
            decision.DetectedEntities is null || decision.DetectedEntities.Count > 20000)
            throw new ArgumentException("Invalid privacy evidence.");
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in decision.DetectedEntities)
        {
            if (entity is null) throw new ArgumentException("Invalid privacy evidence.");
            var category = entity.EntityType is "EMAIL" or "PHONE" or "PAYMENT_CARD" or "SECRET" or "PERSONAL_DATA"
                ? entity.EntityType : "OTHER";
            counts.TryGetValue(category, out var count);
            counts[category] = checked(count + 1);
        }
        var reasonCode = decision.Allowed ? "allowed" : decision.Reason switch
        {
            "detector_failure" => "inspection_failed",
            "required_detector_unavailable" or "privacy_enforcement_unavailable" => "enforcement_unavailable",
            "structured_content_inspection" or "literal_sensitive_value_detected" or "sensitive_boundary_blocked"
                when counts.Count > 0 => "sensitive_detected",
            "structured_content_inspection" or "remote_egress_forbidden" => "policy_blocked",
            _ => "not_recorded",
        };
        return new(eventId, context.Tenant.Value.ToString(), context.Destination, decision.Allowed, decision.Action, reasonCode,
            new ReadOnlyDictionary<string, int>(counts));
    }
}
