using System.Collections.Concurrent;
using OpenConnect.Navigator;
using OpenConnect.Abstractions;

namespace OpenConnect.Navigator.AspNetCore;

/// <summary>Process-local tenant/principal scoped Navigator state.</summary>
public sealed class NavigatorMemoryStore
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NavigatorApproval> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NavigatorCommandOutcome> _commands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);

    internal bool AddPending(string id, NavigatorCommand command, string tenant, string principal, DateTimeOffset expiresAt)
    {
        if (Get(id, tenant, principal) is null || command.NavigatorSessionId != id || string.IsNullOrWhiteSpace(command.CommandId)) return false;
        var key = $"{id}:{command.CommandId}";
        return _pendingCommands.TryAdd(key, new PendingCommand(command, tenant, principal, CommandGrant.ComputeArgsHash(command.Args), expiresAt));
    }

    internal bool ApprovePending(string id, string commandId, string digest, string tenant, string principal, DateTimeOffset now)
    {
        if (!_pendingCommands.TryGetValue($"{id}:{commandId}", out var pending)) return false;
        lock (pending.Gate)
        {
            if (pending.Consumed || pending.ExpiresAt <= now || pending.Tenant != tenant || pending.Principal != principal ||
                !DigestMatches(pending.ArgsHash, digest)) return false;
            pending.Approved = true;
            pending.ApprovalId ??= Ulid.NewUlid().ToString();
            pending.ExpiresAt = now.AddMinutes(5);
            return true;
        }
    }

    internal NavigatorCommand? ConsumeApproved(string id, string commandId, string tenant, string principal, DateTimeOffset now)
    {
        if (!_pendingCommands.TryGetValue($"{id}:{commandId}", out var pending)) return null;
        lock (pending.Gate)
        {
            if (!pending.Approved || pending.Consumed || pending.ExpiresAt <= now || pending.Tenant != tenant || pending.Principal != principal) return null;
            pending.Consumed = true;
            return pending.Command;
        }
    }

    private static bool DigestMatches(string expected, string actual)
    {
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }

    private sealed class PendingCommand(NavigatorCommand command, string tenant, string principal, string argsHash, DateTimeOffset expiresAt)
    {
        internal object Gate { get; } = new();
        internal NavigatorCommand Command { get; } = command;
        internal string Tenant { get; } = tenant;
        internal string Principal { get; } = principal;
        internal string ArgsHash { get; } = argsHash;
        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        internal string? ApprovalId { get; set; }
        internal bool Approved { get; set; }
        internal bool Consumed { get; set; }
    }

    internal SessionEntry? Get(string id, string tenant, string principal) =>
        _sessions.TryGetValue(id, out var item) && item.Tenant == tenant && item.Principal == principal ? item : null;

    internal SessionEntry Create(string tenant, string principal, string clientInstance, string routeId)
    {
        var id = Ulid.NewUlid().ToString();
        var item = new SessionEntry(id, tenant, principal, clientInstance, routeId);
        _sessions[id] = item;
        return item;
    }

    internal IReadOnlyList<NavigatorEvent> Events(string id, long after)
    {
        var entry = GetAny(id);
        if (entry is null) return [];
        lock (entry.Gate)
            return entry.Events.Where(e => e.Sequence > after).ToArray();
    }

    internal bool AppendEvent(string id, NavigatorEvent value)
    {
        if (!NavigatorEventTypes.IsSafe(value) || value.SessionId != id) return false;
        var entry = GetAny(id);
        if (entry is null) return false;
        lock (entry.Gate)
        {
            if (entry.Events.Any(e => e.EventId == value.EventId)) return true;
            if (entry.Events.Count >= 1000) entry.Events.RemoveAt(0);
            entry.Events.Add(value);
            return true;
        }
    }

    internal bool AddApproval(string id, string approvalId, string digest, DateTimeOffset expiresAt)
    {
        if (GetAny(id) is null || string.IsNullOrWhiteSpace(approvalId) || string.IsNullOrWhiteSpace(digest)) return false;
        return _approvals.TryAdd($"{id}:{approvalId}", new NavigatorApproval(digest, expiresAt));
    }

    internal bool ConsumeApproval(string id, string approvalId, string digest, DateTimeOffset now) =>
        _approvals.TryGetValue($"{id}:{approvalId}", out var approval) && approval.TryConsume(digest, now);

    internal bool Execute(string id, NavigatorCommand command, CommandGrant grant, DateTimeOffset now)
    {
        var session = GetAny(id);
        if (session is null || !session.Grants.TryGetValue(command.CommandId, out var current) || !ReferenceEquals(current, grant) ||
            !grant.IsValidFor(id, session.Tenant, session.Principal, session.ClientInstance, command.CommandId,
                command.Sequence, command.Action, command.TargetId, command.TargetRevision, command.ExpectedVersion, command.Args, now)) return false;
        return session.Consumed.TryAdd(grant.Nonce, 0);
    }

    internal bool IssueGrant(string id, CommandGrant grant)
    {
        var session = GetAny(id);
        return session is not null && grant.NavigatorSessionId == id && grant.Tenant == session.Tenant &&
            grant.Principal == session.Principal && grant.ClientInstance == session.ClientInstance &&
            grant.ExpiresAt > DateTimeOffset.UtcNow && session.Grants.TryAdd(grant.CommandId, grant);
    }

    internal CommandGrant? IssueGrant(string id, NavigatorCommand command, string tenant, string principal,
        NavigatorRegisteredTarget target, DateTimeOffset now)
    {
        var session = Get(id, tenant, principal);
        if (session is null || command.NavigatorSessionId != id || command.Sequence < 0 ||
            string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 128 ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128 ||
            target.Descriptor.TargetId != command.TargetId || target.Revision != command.TargetRevision ||
            !target.Descriptor.AllowedActions.Contains(command.Action) ||
            !session.Grants.TryAdd(command.CommandId, CreateGrant(session, command, tenant, principal, now)))
            return null;

        return session.Grants[command.CommandId];
    }

    private static CommandGrant CreateGrant(SessionEntry session, NavigatorCommand command, string tenant,
        string principal, DateTimeOffset now) => new(command.CommandId, session.Id, command.Sequence,
        command.Action, command.TargetId, command.TargetRevision, CommandGrant.ComputeArgsHash(command.Args),
        tenant, principal, session.ClientInstance, command.ExpectedVersion, now.AddMinutes(1), Ulid.NewUlid().ToString());

    internal SessionEntry? GetAny(string id) => _sessions.GetValueOrDefault(id);

    internal sealed class SessionEntry(string id, string tenant, string principal, string clientInstance, string routeId)
    {
        internal string Id { get; } = id;
        internal string Tenant { get; } = tenant;
        internal string Principal { get; } = principal;
        internal string ClientInstance { get; } = clientInstance;
        internal string RouteId { get; } = routeId;
        internal object Gate { get; } = new();
        internal List<NavigatorEvent> Events { get; } = [];
        internal ConcurrentDictionary<string, CommandGrant> Grants { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, byte> Consumed { get; } = new(StringComparer.Ordinal);
    }
}
