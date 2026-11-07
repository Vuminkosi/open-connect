using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class EndpointContractTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public async Task SessionCreateAndGetResponsesMatchSessionContract()
    {
        var store = new NavigatorMemoryStore();
        var context = Context();
        var created = await Run("Create", new CreateSessionRequest("client", "home"), context, store);
        Assert.Equal(201, created.StatusCode);
        AssertSession(created.Json);
        var id = created.Json.RootElement.GetProperty("sessionId").GetString()!;

        var fetched = await Run("Get", id, context, store);
        Assert.Equal(200, fetched.StatusCode);
        AssertSession(fetched.Json);
        Assert.Equal(id, fetched.Json.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task MutationSubmissionReturnsApprovalRequired428()
    {
        var (store, id, command, registry) = SetupCommand();
        var result = await Run("Command", id, new CommandRequest(command), Context(), store, registry);

        Assert.Equal(428, result.StatusCode);
        Assert.True(result.Json.RootElement.GetProperty("approvalRequired").GetBoolean());
        Assert.Equal(command.CommandId, result.Json.RootElement.GetProperty("pendingCommandId").GetString());
    }

    [Fact]
    public async Task ApprovedMutationExecutesOnceAndReturnsContractConformantReceiptAndGrant()
    {
        var (store, id, command, registry) = SetupCommand();
        var context = Context();
        var pending = await Run("Command", id, new CommandRequest(command), context, store, registry);
        Assert.Equal(428, pending.StatusCode);

        var approval = await Run("Approval", id, new ApprovalRequest(command.CommandId, CommandGrant.ComputeArgsHash(command.Args)), context, store);
        Assert.Equal(200, approval.StatusCode);
        var executed = await Run("ExecuteApproved", id, command.CommandId, context, store, registry);
        Assert.Equal(200, executed.StatusCode);

        var receipt = executed.Json.RootElement.GetProperty("receipt");
        AssertContract("receipt.json", receipt);
        Assert.Equal("succeeded", receipt.GetProperty("outcome").GetString());
        var grant = executed.Json.RootElement.GetProperty("grant");
        AssertContract("command.json", grant);
        Assert.Equal("fill", grant.GetProperty("action").GetString());

        var replay = await Run("ExecuteApproved", id, command.CommandId, context, store, registry);
        Assert.Equal(403, replay.StatusCode);
    }

    [Fact]
    public async Task CreateResponseHasNoFieldsOutsideSessionContract()
    {
        var response = await Run("Create", new CreateSessionRequest("client", "home"), Context(), new NavigatorMemoryStore());
        AssertContract("session.json", response.Json.RootElement);
    }

    private static (NavigatorMemoryStore Store, string Id, NavigatorCommand Command, NavigatorTargetRegistry Registry) SetupCommand()
    {
        var store = new NavigatorMemoryStore();
        var registry = new NavigatorTargetRegistry();
        var session = typeof(NavigatorMemoryStore).GetMethod("Create", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [Tenant, Principal, "client", "home"])!;
        var id = (string)session.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var descriptor = new NavigatorTargetDescriptor(NavigatorTargetRegistry.CurrentSchemaVersion, "target", "component", 1,
            "Target", "Description", "home", new HashSet<NavigatorActionType> { NavigatorActionType.Fill }, new HashSet<string>(), "public");
        registry.Register(descriptor, Tenant, Principal, 1);
        return (store, id, new NavigatorCommand("cmd-1", id, 1, "idem-1", 1, NavigatorActionType.Fill, "target", 1, new { value = "safe" }), registry);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, Principal)
        ], "test"));
        context.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int StatusCode, JsonDocument Json)> Run(string name, params object[] args)
    {
        var method = typeof(NavigatorEndpoints).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (IResult)method.Invoke(null, args)!;
        var context = args.OfType<HttpContext>().Single();
        context.Response.Body.Position = 0;
        context.Response.Body.SetLength(0);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await JsonDocument.ParseAsync(context.Response.Body));
    }

    private static void AssertSession(JsonDocument json) => AssertContract("session.json", json.RootElement);

    private static void AssertContract(string file, JsonElement value)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(ContractPath(file)));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var properties = schema.RootElement.GetProperty("properties");
        var actual = value.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(required.IsSubsetOf(actual), $"Missing required properties for {file}: {string.Join(", ", required.Except(actual))}");
        Assert.True(actual.IsSubsetOf(properties.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal)), $"Unexpected properties for {file}.");
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var definition)) continue;
            if (definition.TryGetProperty("enum", out var allowed))
                Assert.Contains(property.Value.GetString(), allowed.EnumerateArray().Select(x => x.GetString()));
        }
    }

    private static string ContractPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "navigator", "v1", file))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "contracts", "navigator", "v1", file);
    }
}
