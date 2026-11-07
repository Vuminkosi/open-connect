using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using System.Security.Claims;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class NavigatorWsProjectorTests
{
    private static readonly NavigatorServerAuthContext Authorized = NavigatorServerAuthContext.FromServerPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "tenant"), new Claim("sub", "principal")], "test")), "session");

    [Fact]
    public async Task ProjectsCommittedEventOnlyOnce()
    {
        var projector = new NavigatorWsProjector();
        var value = Event("event-1");

        Assert.NotNull(await projector.ProjectAsync(value, Authorized, consumerId: "consumer-1"));
        Assert.Null(await projector.ProjectAsync(value, Authorized, consumerId: "consumer-1"));
    }

    [Fact]
    public async Task Deduplicates_per_consumer_not_per_projector()
    {
        var projector = new NavigatorWsProjector();
        var value = Event("shared-event");

        Assert.NotNull(await projector.ProjectAsync(value, Authorized, consumerId: "consumer-1"));
        Assert.NotNull(await projector.ProjectAsync(value, Authorized, consumerId: "consumer-2"));
    }

    [Fact]
    public async Task RejectsUnsafeEvent()
    {
        var projector = new NavigatorWsProjector();

        await Assert.ThrowsAsync<ArgumentException>(() => projector.ProjectAsync(
            Event("event-2") with { Metadata = new Dictionary<string, string> { ["html"] = "<div>" } }, Authorized, consumerId: "consumer-1"));
    }

    [Fact]
    public async Task DeniesOtherTenantOrPrincipal()
    {
        var projector = new NavigatorWsProjector();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projector.ProjectAsync(
            Event("event-3"), default, consumerId: "consumer-1"));
    }

    [Fact]
    public async Task RejectsInvalidConsumerId()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new NavigatorWsProjector().ProjectAsync(
            Event("event-invalid-consumer"), Authorized, consumerId: "bad consumer"));
    }

    [Fact]
    public async Task DeliveredSetEvictsOldestEntryAtCapacity()
    {
        var projector = new NavigatorWsProjector();
        for (var index = 0; index <= 10_000; index++)
            Assert.NotNull(await projector.ProjectAsync(Event($"event-{index}"), Authorized, consumerId: "consumer"));
        Assert.NotNull(await projector.ProjectAsync(Event("event-0"), Authorized, consumerId: "consumer"));
    }

    [Fact]
    public async Task ConsumerSessionKeyLimitEvictsOldestKey()
    {
        var projector = new NavigatorWsProjector();
        for (var index = 0; index < 1_000; index++)
            Assert.NotNull(await projector.ProjectAsync(Event("same-event"), Authorized, consumerId: $"consumer-{index}"));

        Assert.NotNull(await projector.ProjectAsync(Event("same-event"), Authorized, consumerId: "consumer-new"));
        Assert.NotNull(await projector.ProjectAsync(Event("same-event"), Authorized, consumerId: "consumer-0"));
    }

    [Fact]
    public void AuthContextRequiresAuthenticatedServerClaims()
    {
        Assert.Throws<UnauthorizedAccessException>(() => NavigatorServerAuthContext.FromServerPrincipal(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "tenant"), new Claim("sub", "subject")])), "session"));
        Assert.DoesNotContain(typeof(NavigatorServerAuthContext).GetConstructors(), constructor => constructor.IsPublic);
    }

    [Fact]
    public async Task DeniesEventForAnotherSession()
    {
        var projector = new NavigatorWsProjector();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projector.ProjectAsync(
            Event("event-session-mismatch") with { SessionId = "another-session" }, Authorized, consumerId: "consumer-1"));
    }

    private static NavigatorEvent Event(string id) =>
        new(id, "command.completed", "session", null, 1, null, DateTimeOffset.UtcNow);
}
