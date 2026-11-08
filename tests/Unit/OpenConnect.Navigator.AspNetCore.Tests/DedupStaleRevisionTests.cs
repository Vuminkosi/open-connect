using System.Reflection;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class DedupStaleRevisionTests
{
    [Fact]
    public void DuplicateEventIdIsStoredOnce()
    {
        var (store, sessionId) = CreateSession();
        var value = Event("event-1", sessionId);

        Assert.True(Append(store, sessionId, value));
        Assert.True(Append(store, sessionId, value with { Sequence = 2 }));

        Assert.Single(Events(store, sessionId));
        Assert.Equal(1, Assert.Single(Events(store, sessionId)).Sequence);
    }

    [Fact]
    public async Task ConcurrentDuplicateEventAppendsAreStoredOnce()
    {
        var (store, sessionId) = CreateSession();
        var value = Event("event-concurrent", sessionId);

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => Append(store, sessionId, value))));

        Assert.All(results, Assert.True);
        Assert.Single(Events(store, sessionId));
    }

    [Fact]
    public void GrantForStaleTargetRevisionIsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new NavigatorTargetRegistry();
        registry.Register(Target(), "tenant", "principal", 2);
        var args = "{}";
        var grant = Grant(targetRevision: 1, args, now);

        Assert.False(registry.TryValidate(grant, args, "tenant", "principal", "session", 1, now, out _));
    }

    [Fact]
    public void RemountRevisionBumpInvalidatesPreviouslyGrantedMutation()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new NavigatorTargetRegistry();
        registry.Register(Target(), "tenant", "principal", 1);
        var args = "{}";
        var grant = Grant(targetRevision: 1, args, now);
        Assert.True(registry.TryValidate(grant, args, "tenant", "principal", "session", 1, now, out _));

        registry.Register(Target(), "tenant", "principal", 2);

        Assert.Equal(2, Assert.Single(registry.Resolve("target",
            new("tenant", "principal", "/edit", new HashSet<string>()))).Revision);
        Assert.False(registry.TryValidate(grant, args, "tenant", "principal", "session", 1, now, out _));
    }

    private static (NavigatorMemoryStore Store, string SessionId) CreateSession()
    {
        var store = new NavigatorMemoryStore();
        var session = Invoke(store, "Create", "tenant", "principal", "client", "/edit")!;
        return (store, (string)session.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!);
    }

    private static bool Append(NavigatorMemoryStore store, string sessionId, NavigatorEvent value) =>
        (bool)Invoke(store, "AppendEvent", sessionId, value)!;

    private static IReadOnlyList<NavigatorEvent> Events(NavigatorMemoryStore store, string sessionId) =>
        (IReadOnlyList<NavigatorEvent>)Invoke(store, "Events", sessionId, 0L)!;

    private static NavigatorEvent Event(string id, string sessionId) =>
        new(id, "command.completed", sessionId, null, 1, null, DateTimeOffset.UtcNow);

    private static NavigatorTargetDescriptor Target() =>
        new(NavigatorTargetRegistry.CurrentSchemaVersion, "target", "form", 1, "Save", "Save form", "/edit",
            new HashSet<NavigatorActionType> { NavigatorActionType.Click }, new HashSet<string>(), "normal");

    private static CommandGrant Grant(long targetRevision, string args, DateTimeOffset now)
    {
        using var document = System.Text.Json.JsonDocument.Parse(args);
        return new CommandGrant("cmd", "session", 1, NavigatorActionType.Click, "target", targetRevision,
            CommandGrant.ComputeArgsHash(document.RootElement), "tenant", "principal", "client", 1,
            now.AddMinutes(1), "nonce");
    }

    private static object? Invoke(NavigatorMemoryStore store, string method, params object[] args) =>
        typeof(NavigatorMemoryStore).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, args);
}
