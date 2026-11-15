namespace OpenConnect.LiveUI.Agents;

/// <summary>Trusted, host-derived context for one active LiveUI agent invocation.</summary>
public sealed record LiveUIAgentContext(string SessionId, string CurrentRouteId);

/// <summary>Resolves the active user session and route without accepting either value from model input.</summary>
public interface ILiveUIAgentContextAccessor
{
    ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optionally binds a trusted host session to the current asynchronous tool invocation.</summary>
public interface ILiveUIAgentContextSessionBinder
{
    IDisposable BindSession(string sessionId);
}
