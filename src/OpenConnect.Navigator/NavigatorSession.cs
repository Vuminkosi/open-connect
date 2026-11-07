namespace OpenConnect.Navigator;

public sealed class NavigatorSession(string tenantId, string principalId)
{
    private static readonly IReadOnlyDictionary<NavigatorSessionState, NavigatorSessionState[]> Transitions = new Dictionary<NavigatorSessionState, NavigatorSessionState[]>
    {
        [NavigatorSessionState.Created] = [NavigatorSessionState.Pairing, NavigatorSessionState.Ready, NavigatorSessionState.Cancelled, NavigatorSessionState.Failed, NavigatorSessionState.Expired],
        [NavigatorSessionState.Pairing] = [NavigatorSessionState.Ready, NavigatorSessionState.Paused, NavigatorSessionState.Cancelled, NavigatorSessionState.Failed, NavigatorSessionState.Expired],
        [NavigatorSessionState.Ready] = [NavigatorSessionState.Running, NavigatorSessionState.Cancelled, NavigatorSessionState.Failed, NavigatorSessionState.Expired],
        [NavigatorSessionState.Running] = [NavigatorSessionState.AwaitingApproval, NavigatorSessionState.Paused, NavigatorSessionState.Completing, NavigatorSessionState.Failed, NavigatorSessionState.Cancelled, NavigatorSessionState.Expired],
        [NavigatorSessionState.AwaitingApproval] = [NavigatorSessionState.Running, NavigatorSessionState.Paused, NavigatorSessionState.Cancelled, NavigatorSessionState.Failed, NavigatorSessionState.Expired],
        [NavigatorSessionState.Paused] = [NavigatorSessionState.Running, NavigatorSessionState.Cancelled, NavigatorSessionState.Failed, NavigatorSessionState.Expired],
        [NavigatorSessionState.Completing] = [NavigatorSessionState.Completed, NavigatorSessionState.Failed]
    };
    public string TenantId { get; } = tenantId;
    public string PrincipalId { get; } = principalId;
    public string? SessionId { get; private set; }
    public string? ClientInstance { get; private set; }
    public string? RouteId { get; private set; }
    public NavigatorSessionState State { get; private set; } = NavigatorSessionState.Created;
    public long Revision { get; private set; }

    public static NavigatorSession Restore(NavigatorSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        return new NavigatorSession(snapshot.TenantId, snapshot.PrincipalId)
        {
            SessionId = snapshot.SessionId,
            ClientInstance = snapshot.ClientInstance,
            RouteId = snapshot.RouteId,
            State = snapshot.State,
            Revision = snapshot.Revision
        };
    }

    public void TransitionTo(NavigatorSessionState next)
    {
        if (!Enum.IsDefined(next) || !Transitions.TryGetValue(State, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"Invalid Navigator session transition: {State} -> {next}.");
        var nextRevision = checked(Revision + 1);
        State = next;
        Revision = nextRevision;
    }
}
