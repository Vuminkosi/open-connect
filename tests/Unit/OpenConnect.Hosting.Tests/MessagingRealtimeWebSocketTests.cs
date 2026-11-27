using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public sealed class MessagingRealtimeWebSocketTests
{
    [Theory]
    [InlineData("?access_token=secret")]
    [InlineData("?token=secret")]
    [InlineData("?bearer=secret")]
    public async Task HandleAsync_RejectsTokensInQueryString(string queryString)
    {
        var services = new ServiceCollection();
        services.AddOpenConnectMessagingHosting(o => o.Enabled = true);
        using var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };
        context.Request.QueryString = new QueryString(queryString);

        await MessagingRealtimeWebSocketHandler.HandleAsync(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_Returns503WhenDisabled()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectMessagingHosting(o => o.Enabled = false);
        using var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };

        await MessagingRealtimeWebSocketHandler.HandleAsync(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public void IsOriginAllowed_RejectsDisallowedCrossOriginBrowserRequest()
    {
        var options = new MessagingHostingOptions();
        options.AllowedWebSocketOrigins.Add("https://app.company.example");

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.company.example");

        Assert.True(MessagingRealtimeWebSocketHandler.IsOriginAllowed(context, options, "https://app.company.example"));
        Assert.False(MessagingRealtimeWebSocketHandler.IsOriginAllowed(context, options, "https://evil.example"));
    }

    [Fact]
    public async Task WebSocketTicketService_EnforcesSingleUseExpiryAndOriginBinding()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-25T12:00:00Z"));
        var options = Options.Create(new MessagingHostingOptions { WebSocketTicketLifetime = TimeSpan.FromSeconds(30) });
        var service = new InMemoryMessagingWebSocketTicketService(options, clock);

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var device = new DeviceId(Ulid.NewUlid());

        var issued = await service.IssueAsync(tenant, "app", principal, device, "https://app.company.example");

        // Wrong origin rejected (and single-use ticket consumed)
        var wrongOrigin = await service.TryConsumeAsync(issued.Ticket, "https://evil.example");
        Assert.Null(wrongOrigin);

        // Replay rejected
        var replay = await service.TryConsumeAsync(issued.Ticket, "https://app.company.example");
        Assert.Null(replay);

        // Valid single-use consume
        var issued2 = await service.IssueAsync(tenant, "app", principal, device, "https://app.company.example");
        var consumed = await service.TryConsumeAsync(issued2.Ticket, "https://app.company.example");
        Assert.NotNull(consumed);
        Assert.Equal(tenant, consumed!.TenantId);
        Assert.Equal(principal, consumed.PrincipalId);
        Assert.Equal(device, consumed.DeviceId);

        // Expired ticket rejected
        var issued3 = await service.IssueAsync(tenant, "app", principal, device, null);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await service.TryConsumeAsync(issued3.Ticket, null));
    }

    [Fact]
    public async Task RealtimeHub_DisconnectsSlowConsumerAndClosesRevokedDeviceImmediately()
    {
        var options = Options.Create(new MessagingHostingOptions { MaxWebSocketQueueDepth = 4 });
        var hub = new MessagingRealtimeHub(options);
        var tenant = new TenantId(Ulid.NewUlid());
        var otherTenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var device1 = new DeviceId(Ulid.NewUlid());
        var device2 = new DeviceId(Ulid.NewUlid());

        using var conn1 = hub.RegisterConnection(tenant, principal, device1, CancellationToken.None);
        using var conn2 = hub.RegisterConnection(tenant, principal, device2, CancellationToken.None);
        Assert.Equal(2, hub.ActiveConnectionCount);

        // Cross-tenant publish throws 403
        var crossTenantEvent = new MessagingEventEnvelope(1, "ev-x", MessagingEventTypes.MessageCommitted, otherTenant, null, 1, "c1", DateTimeOffset.UtcNow, "{}");
        await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await hub.PublishToPrincipalAsync(tenant, principal, crossTenantEvent));

        // Revoking device1 closes ONLY device1 immediately
        var closed = await hub.CloseDeviceConnectionsAsync(tenant, device1, "device_revoked");
        Assert.Equal(1, closed);
        Assert.True(conn1.Token.IsCancellationRequested);
        Assert.Equal("device_revoked", conn1.CloseReason);
        Assert.False(conn2.Token.IsCancellationRequested);

        // Overflowing conn2's bounded queue (capacity 4) disconnects slow consumer for replay
        for (var i = 1; i <= 5; i++)
        {
            var ev = new MessagingEventEnvelope(1, $"ev-{i}", MessagingEventTypes.MessageCommitted, tenant, null, i, $"c-{i}", DateTimeOffset.UtcNow, "{}");
            await hub.PublishToPrincipalAsync(tenant, principal, ev);
        }

        Assert.True(conn2.Token.IsCancellationRequested);
        Assert.Equal("slow_consumer_replay_required", conn2.CloseReason);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
