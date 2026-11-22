using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Blazor;

/// <summary>Resolves only sessions owned by the configured same-host development identity.</summary>
public sealed class SameHostCloudLiveUISessionContextProvider(
    InMemorySessionStore sessions, TodoGatewayOptions options) : ICloudLiveUISessionContextProvider
{
    public ValueTask<EgressContext?> GetContextAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode != TodoGatewayMode.SameHostGateway)
            return ValueTask.FromResult<EgressContext?>(null);
        try
        {
            var session = sessions.Get(Ulid.Parse(sessionId));
            if (session is null || session.TenantId.Value != Ulid.Parse(options.TenantId) ||
                session.PrincipalId.Value != Ulid.Parse(options.PrincipalId))
                return ValueTask.FromResult<EgressContext?>(null);
            return ValueTask.FromResult<EgressContext?>(new(session.TenantId, session.PrincipalId, session.Id, EgressDestination.ExternalModel));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            return ValueTask.FromResult<EgressContext?>(null);
        }
    }
}
