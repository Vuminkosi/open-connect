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
    public NavigatorSessionState State { get; private set; } = NavigatorSessionState.Created;
    public void TransitionTo(NavigatorSessionState next)
    {
        if (!Enum.IsDefined(next) || !Transitions.TryGetValue(State, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"Invalid Navigator session transition: {State} -> {next}.");
        State = next;
    }
}
