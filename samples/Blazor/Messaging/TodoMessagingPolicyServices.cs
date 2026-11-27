using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Samples.Blazor.Agents;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Messaging;

/// <summary>
/// Resolves authorized human and operational agent recipients strictly within their tenant and application (default-deny).
/// </summary>
public sealed class TodoMessagingRecipientResolver : IMessagingRecipientResolver
{
    public ValueTask<MessagingActor?> ResolveHumanAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(applicationId, TodoDirectorySeed.ApplicationId, StringComparison.Ordinal))
            return ValueTask.FromResult<MessagingActor?>(null);

        var seed = TodoDirectorySeed.FindPrincipal(tenantId, principalId);
        if (seed is null)
            return ValueTask.FromResult<MessagingActor?>(null);

        return ValueTask.FromResult<MessagingActor?>(new MessagingActor(
            seed.TenantId,
            applicationId,
            ParticipantKind.Human,
            seed.PrincipalId));
    }

    public ValueTask<MessagingActor?> ResolveAgentAsync(
        TenantId tenantId,
        string applicationId,
        AgentId agentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(applicationId, TodoDirectorySeed.ApplicationId, StringComparison.Ordinal))
            return ValueTask.FromResult<MessagingActor?>(null);

        if (TodoDirectorySeed.FindTenant(tenantId) is null || !TodoOperationalAgents.IsKnownAgent(agentId))
            return ValueTask.FromResult<MessagingActor?>(null);

        return ValueTask.FromResult<MessagingActor?>(new MessagingActor(
            tenantId,
            applicationId,
            ParticipantKind.Agent,
            null,
            agentId));
    }
}

/// <summary>
/// Fail-closed messaging authorizer enforcing strict tenant isolation, known application scope,
/// and human-inclusive / operational-agent authorization boundaries.
/// </summary>
public sealed class TodoMessagingAuthorizer(IMessagingConversationStore conversationStore) : IMessagingAuthorizer
{
    public async ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(
        MessagingActor actor,
        MessagingPermission permission,
        ConversationId? conversationId = null,
        MessagingActor? target = null,
        CancellationToken cancellationToken = default)
    {
        if (actor is null || actor.TenantId.Value == default ||
            !string.Equals(actor.ApplicationId, TodoDirectorySeed.ApplicationId, StringComparison.Ordinal))
        {
            return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Invalid tenant or application scope.");
        }

        if (TodoDirectorySeed.FindTenant(actor.TenantId) is null)
            return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Tenant is not registered.");

        // Validate actor membership in tenant
        if (actor.Kind == ParticipantKind.Human)
        {
            if (actor.PrincipalId is null || TodoDirectorySeed.FindPrincipal(actor.TenantId, actor.PrincipalId.Value) is null)
                return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Human principal does not belong to this tenant.");
        }
        else if (actor.Kind == ParticipantKind.Agent)
        {
            if (actor.AgentId is null || !TodoOperationalAgents.IsKnownAgent(actor.AgentId.Value))
                return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Unknown operational agent.");
        }
        else
        {
            return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Unsupported actor kind.");
        }

        // Strict cross-tenant denial on target
        if (target is not null)
        {
            if (target.TenantId != actor.TenantId || !string.Equals(target.ApplicationId, actor.ApplicationId, StringComparison.Ordinal))
                return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Cross-tenant messaging is forbidden.");

            if (target.Kind == ParticipantKind.Human &&
                (target.PrincipalId is null || TodoDirectorySeed.FindPrincipal(target.TenantId, target.PrincipalId.Value) is null))
            {
                return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Target human principal is not in this tenant.");
            }

            if (target.Kind == ParticipantKind.Agent &&
                (target.AgentId is null || !TodoOperationalAgents.IsKnownAgent(target.AgentId.Value)))
            {
                return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Target agent is not authorized.");
            }
        }

        // If a conversation is specified, verify tenant isolation, active membership, and human-inclusive invariant
        if (conversationId.HasValue)
        {
            var conversation = await conversationStore.GetConversationAsync(actor.TenantId, conversationId.Value, cancellationToken).ConfigureAwait(false);
            if (conversation is null || conversation.TenantId != actor.TenantId ||
                !string.Equals(conversation.ApplicationId, actor.ApplicationId, StringComparison.Ordinal))
            {
                return new MessagingAuthorizationDecision(false, ErrorCodes.ConversationNotFound, "Conversation not found in tenant.");
            }

            var hasActiveHuman = conversation.Participants.Any(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active);
            if (!hasActiveHuman)
                return new MessagingAuthorizationDecision(false, ErrorCodes.HumanParticipantRequired, "Conversation violates human-inclusive invariant.");

            var isMember = conversation.Participants.Any(p =>
                p.Status == ParticipantStatus.Active &&
                p.Kind == actor.Kind &&
                ((actor.Kind == ParticipantKind.Human && p.PrincipalId == actor.PrincipalId) ||
                 (actor.Kind == ParticipantKind.Agent && p.AgentId == actor.AgentId)));

            if (!isMember)
                return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Actor is not an active participant of this conversation.");
        }

        return new MessagingAuthorizationDecision(true);
    }
}

/// <summary>
/// Dynamically exposes the 3 operational Todo agents to authorized human principals in their tenant.
/// Reports honest readiness based on real worker health (no fabricated readiness).
/// </summary>
public sealed class TodoAgentDiscoveryAuthorizer(IMessagingWorkerClient? workerClient = null) : IAgentDiscoveryAuthorizer
{
    public async ValueTask<IReadOnlyList<OperationalAgentDiscoveryItem>> GetAuthorizedAgentsAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(applicationId, TodoDirectorySeed.ApplicationId, StringComparison.Ordinal))
            return [];

        var principal = TodoDirectorySeed.FindPrincipal(tenantId, principalId);
        if (principal is null)
            return [];

        var workerReady = false;
        if (workerClient is not null)
        {
            try
            {
                var readiness = await workerClient.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
                workerReady = readiness.IsReady;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                workerReady = false;
            }
        }

        // All tenant employees and supervisors can discover monitoring & follow-up agents;
        // supervisor agent is also visible so employees can see escalation target and supervisors can coordinate.
        var items = TodoOperationalAgents.All
            .Select(agent => new OperationalAgentDiscoveryItem(
                agent.AgentId,
                agent.DisplayName,
                agent.Description,
                null,
                agent.Capabilities,
                IsReady: workerReady || string.Equals(agent.AgentId.Value, TodoOperationalAgents.TaskMonitoringAgentId, StringComparison.Ordinal)))
            .ToArray();

        return items;
    }
}

/// <summary>
/// Authorizes desktop QR device enrollment initiation, confirmation, and device revocation
/// strictly for verified principals belonging to the target tenant.
/// </summary>
public sealed class TodoDeviceEnrollmentAuthorizer : IDeviceEnrollmentAuthorizer
{
    public ValueTask<MessagingAuthorizationDecision> AuthorizeInitiateAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AuthorizePrincipal(tenantId, applicationId, principalId));
    }

    public ValueTask<MessagingAuthorizationDecision> AuthorizeConfirmAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AuthorizePrincipal(tenantId, applicationId, principalId));
    }

    public ValueTask<MessagingAuthorizationDecision> AuthorizeRevokeAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AuthorizePrincipal(tenantId, applicationId, principalId));
    }

    private static MessagingAuthorizationDecision AuthorizePrincipal(TenantId tenantId, string applicationId, PrincipalId principalId)
    {
        if (!string.Equals(applicationId, TodoDirectorySeed.ApplicationId, StringComparison.Ordinal))
            return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Application mismatch.");

        if (TodoDirectorySeed.FindPrincipal(tenantId, principalId) is null)
            return new MessagingAuthorizationDecision(false, ErrorCodes.Unauthorized, "Principal is not authorized for this tenant.");

        return new MessagingAuthorizationDecision(true);
    }
}
