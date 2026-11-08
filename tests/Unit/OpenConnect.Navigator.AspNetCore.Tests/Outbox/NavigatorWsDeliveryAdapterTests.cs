using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class NavigatorWsDeliveryAdapterTests
{
    [Fact]
    public async Task ProjectAsyncRequiresMatchingTenantPrincipalAndSession()
    {
        var adapter = new NavigatorWsDeliveryAdapter(new NavigatorWsProjector());
        var authenticatedServerContext = NavigatorServerAuthContext.FromServerPrincipal(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim("tenant_id", "tenant-1"), new System.Security.Claims.Claim("sub", "principal-1")], "test")), "session-1");

        Assert.NotNull(await adapter.ProjectAsync(Event(), authenticatedServerContext, "consumer-1"));
        Assert.Throws<UnauthorizedAccessException>(() => NavigatorServerAuthContext.FromServerPrincipal(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim("sub", "principal-1")], "test")), "session-1"));
        Assert.Throws<UnauthorizedAccessException>(() => NavigatorServerAuthContext.FromServerPrincipal(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim("tenant_id", "tenant-1")], "test")), "session-1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ProjectAsync(
            Event("session-2"), authenticatedServerContext, "consumer-2"));
    }

    [Fact]
    public Task ProgressIsExplicitlyEphemeral()
    {
        var progress = NavigatorWsDeliveryAdapter.ProjectProgress("session-1", 1, "progress", new Dictionary<string, string>());
        var json = System.Text.Json.JsonSerializer.SerializeToElement(progress);

        Assert.False(json.GetProperty("durable").GetBoolean());
        Assert.False(json.GetProperty("replayable").GetBoolean());
        return Task.CompletedTask;
    }

    private static NavigatorEvent Event(string sessionId = "session-1") =>
        new("event-1", "command.completed", sessionId, "task-1", 1, null, DateTimeOffset.UtcNow);
}
