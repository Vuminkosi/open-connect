using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Blazor;

/// <summary>Development-only planner bound to a session owned by the same-host identity.</summary>
public sealed class SameHostGatewayPlanner(
    InMemorySessionStore sessions,
    TodoGatewayOptions options,
    ILiveUIAgentContextAccessor contextAccessor,
    IChatClient chatClient,
    IEgressGate egressGate) : ILiveUIGatewayInferenceProvider
{
    public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;

    public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available));
    }

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode != TodoGatewayMode.SameHostGateway)
            throw new UnauthorizedAccessException("The interactive session is unavailable.");
        var current = await contextAccessor.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || !string.Equals(current.SessionId, request.SessionId, StringComparison.Ordinal) ||
            !string.Equals(current.CurrentRouteId, request.RouteId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The interactive session is unavailable.");
        Ulid sessionId;
        try { sessionId = Ulid.Parse(request.SessionId); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        { throw new UnauthorizedAccessException("The interactive session is unavailable."); }

        var session = sessions.Get(sessionId);
        if (session is null || session.TenantId.Value != Ulid.Parse(options.TenantId) ||
            session.PrincipalId.Value != Ulid.Parse(options.PrincipalId))
            throw new UnauthorizedAccessException("The interactive session is unavailable.");

        var context = new EgressContext(session.TenantId, session.PrincipalId, session.Id, EgressDestination.ExternalModel);
        var guarded = new FirewallGuardedChatClient(chatClient, egressGate, new FixedEgressContextProvider(context));
        return await new FirewallGuardedLiveUIGatewayInferenceProvider(guarded)
            .GeneratePlanAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
