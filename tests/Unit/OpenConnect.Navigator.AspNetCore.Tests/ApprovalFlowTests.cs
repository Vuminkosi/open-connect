using System.Reflection;
using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class ApprovalFlowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string Tenant = "tenant-a";
    private const string Principal = "principal-a";

    [Fact]
    public void MutationWithoutApprovalReturns428AndRemainsPending()
    {
        var (store, id, command) = Setup();
        Assert.True(AddPending(store, id, command));
        Assert.Null(Consume(store, id, command.CommandId));
    }

    [Fact]
    public void ApprovalAllowsExactlyOneExecution()
    {
        var (store, id, command) = Setup();
        Assert.True(AddPending(store, id, command));
        var hash = CommandGrant.ComputeArgsHash(command.Args);
        Assert.True(Approve(store, id, command.CommandId, hash, Tenant, Principal, Now));
        Assert.Equal(command, Consume(store, id, command.CommandId));
    }

    [Fact]
    public void ReplayedExecutionIsDenied()
    {
        var (store, id, command) = Setup();
        Assert.True(AddPending(store, id, command));
        Assert.True(Approve(store, id, command.CommandId, CommandGrant.ComputeArgsHash(command.Args), Tenant, Principal, Now));
        Assert.NotNull(Consume(store, id, command.CommandId));
        Assert.Null(Consume(store, id, command.CommandId));
    }

    [Fact]
    public void ApprovalForAnotherCommandOrWrongHashIsDenied()
    {
        var (store, id, command) = Setup();
        Assert.True(AddPending(store, id, command));
        Assert.False(Approve(store, id, "different-command", CommandGrant.ComputeArgsHash(command.Args), Tenant, Principal, Now));
        Assert.False(Approve(store, id, command.CommandId, "00", Tenant, Principal, Now));
        Assert.Null(Consume(store, id, command.CommandId));
    }

    private static (NavigatorMemoryStore Store, string SessionId, NavigatorCommand Command) Setup()
    {
        var store = new NavigatorMemoryStore();
        var session = Invoke(store, "Create", Tenant, Principal, "client", "home")!;
        var id = (string)session.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var command = new NavigatorCommand("cmd-1", id, 1, "idem-1", 1, NavigatorActionType.Fill,
            "target", 1, new { value = "safe" });
        return (store, id, command);
    }

    private static bool AddPending(NavigatorMemoryStore store, string id, NavigatorCommand command) =>
        (bool)Invoke(store, "AddPending", id, command, Tenant, Principal, Now.AddMinutes(5))!;

    private static bool Approve(NavigatorMemoryStore store, string id, string commandId, string hash, string tenant, string principal, DateTimeOffset now) =>
        (bool)Invoke(store, "ApprovePending", id, commandId, hash, tenant, principal, now)!;

    private static NavigatorCommand? Consume(NavigatorMemoryStore store, string id, string commandId) =>
        (NavigatorCommand?)Invoke(store, "ConsumeApproved", id, commandId, Tenant, Principal, Now);

    private static object? Invoke(NavigatorMemoryStore store, string method, params object[] args) =>
        typeof(NavigatorMemoryStore).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, args);
}
