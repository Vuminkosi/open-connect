using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class NavigatorWsProjectorTests
{
    private static readonly NavigatorWsAuthorization Authorized = new("tenant", "principal", "tenant", "principal", "session");

    [Fact]
    public async Task ProjectsCommittedEventOnlyOnce()
    {
        var projector = new NavigatorWsProjector();
        var value = Event("event-1");

        Assert.NotNull(await projector.ProjectAsync(value, Authorized));
        Assert.Null(await projector.ProjectAsync(value, Authorized));
    }

    [Fact]
    public async Task RejectsUnsafeEvent()
    {
        var projector = new NavigatorWsProjector();

        await Assert.ThrowsAsync<ArgumentException>(() => projector.ProjectAsync(
            Event("event-2") with { Metadata = new Dictionary<string, string> { ["html"] = "<div>" } }, Authorized));
    }

    [Fact]
    public async Task DeniesOtherTenantOrPrincipal()
    {
        var projector = new NavigatorWsProjector();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projector.ProjectAsync(
            Event("event-3"), Authorized with { SessionTenantId = "other" }));
    }

    [Fact]
    public async Task DeniesEventForAnotherSession()
    {
        var projector = new NavigatorWsProjector();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projector.ProjectAsync(
            Event("event-session-mismatch") with { SessionId = "another-session" }, Authorized));
    }

    private static NavigatorEvent Event(string id) =>
        new(id, "command.completed", "session", null, 1, null, DateTimeOffset.UtcNow);
}
