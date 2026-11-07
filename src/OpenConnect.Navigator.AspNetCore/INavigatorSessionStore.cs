namespace OpenConnect.Navigator.AspNetCore;

using OpenConnect.Navigator;

/// <summary>
/// Durability capability marker for the Navigator session store boundary. A durable implementation
/// must be backed by the application's operational persistence adapter, not a Navigator-owned database.
/// </summary>
public interface INavigatorSessionStore
{
    /// <summary>Gets whether stored sessions and related operational state survive process restarts.</summary>
    bool IsDurable { get; }

    NavigatorSession? Get(string id, string tenant, string principal);
    NavigatorSession Create(string tenant, string principal, string clientInstance, string routeId);
    bool AddPending(string id, NavigatorCommand command, string tenant, string principal, DateTimeOffset expiresAt);
    bool ApprovePending(string id, string commandId, string digest, string tenant, string principal, DateTimeOffset now);
    NavigatorCommand? ConsumeApproved(string id, string commandId, string tenant, string principal, DateTimeOffset now);
    CommandGrant? IssueGrant(string id, NavigatorCommand command, string tenant, string principal, NavigatorRegisteredTarget target, DateTimeOffset now);
    bool Execute(string id, NavigatorCommand command, CommandGrant grant, DateTimeOffset now);
    IReadOnlyList<NavigatorEvent> Events(string id, long after);
}

/// <summary>A session view returned by a session-store implementation.</summary>
public sealed record NavigatorSession(string Id, string Tenant, string Principal, string ClientInstance, string RouteId);
