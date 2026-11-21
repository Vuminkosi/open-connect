using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Privacy;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class SameHostCloudLiveUISessionTests
{
    [Fact]
    public async Task ResolvesOnlyOwnedSameHostSessions()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var store = new InMemorySessionStore();
        var owned = store.Create(tenant, principal);
        var foreignPrincipal = store.Create(tenant, new PrincipalId(Ulid.NewUlid()));
        var foreignTenant = store.Create(new TenantId(Ulid.NewUlid()), principal);
        var options = new TodoGatewayOptions { Mode = TodoGatewayMode.SameHostGateway,
            TenantId = tenant.Value.ToString(), PrincipalId = principal.Value.ToString() };
        var resolver = new SameHostCloudLiveUISessionContextProvider(store, options);

        var context = await resolver.GetContextAsync(owned.Id.Value.ToString());
        Assert.NotNull(context);
        Assert.Equal(EgressDestination.ExternalModel, context.Destination);
        Assert.Equal(tenant, context.Tenant); Assert.Equal(principal, context.Principal); Assert.Equal(owned.Id, context.Session);
        Assert.Null(await resolver.GetContextAsync(foreignPrincipal.Id.Value.ToString()));
        Assert.Null(await resolver.GetContextAsync(foreignTenant.Id.Value.ToString()));
        Assert.Null(await resolver.GetContextAsync(Ulid.NewUlid().ToString()));
        Assert.Null(await resolver.GetContextAsync("invalid"));
        var external = new SameHostCloudLiveUISessionContextProvider(store, new TodoGatewayOptions { Mode = TodoGatewayMode.ExternalGateway });
        Assert.Null(await external.GetContextAsync(owned.Id.Value.ToString()));
    }
}
