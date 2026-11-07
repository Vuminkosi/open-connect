using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class SessionSeamTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public void NavigatorServicesResolveRegisteredSessionStoreSubstitute()
    {
        var substitute = new StubSessionStore();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentDefinitionRegistry, StubAgentDefinitionRegistry>();
        services.AddOpenConnectNavigator();
        services.AddSingleton<INavigatorSessionStore>(substitute);

        using var provider = services.BuildServiceProvider();

        Assert.Same(substitute, provider.GetRequiredService<INavigatorSessionStore>());
    }

    [Fact]
    public async Task CreateAndGetEndpointsUseInjectedSessionStore()
    {
        const string sessionId = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
        var substitute = new StubSessionStore
        {
            CreatedSession = new NavigatorSession(sessionId, Tenant, Principal, "client", "home"),
            ExistingSession = new NavigatorSession(sessionId, Tenant, Principal, "client", "home")
        };
        var createContext = Context();
        var created = await Run("Create", new CreateSessionRequest("client", "home"), createContext, substitute);
        Assert.Equal(201, created);
        Assert.Equal(1, substitute.CreateCalls);

        var getContext = Context();
        var fetched = await Run("Get", sessionId, getContext, substitute);
        Assert.Equal(200, fetched);
        Assert.Equal(sessionId, substitute.LastGetId);
        Assert.Equal(Tenant, substitute.LastGetTenant);
        Assert.Equal(Principal, substitute.LastGetPrincipal);
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

    private static async Task<int> Run(string name, params object[] args)
    {
        var method = typeof(NavigatorEndpoints).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (IResult)method.Invoke(null, args)!;
        var context = args.OfType<HttpContext>().Single();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private sealed class StubAgentDefinitionRegistry : IAgentDefinitionRegistry
    {
        public void Register(AgentDefinition definition) { }
        public AgentDefinition? Get(AgentId agentId) => null;
        public IReadOnlyList<AgentDefinition> GetAll() => Array.Empty<AgentDefinition>();
    }

    private sealed class StubSessionStore : INavigatorSessionStore
    {
        public bool IsDurable => false;
        public NavigatorSession? CreatedSession { get; init; }
        public NavigatorSession? ExistingSession { get; init; }
        public int CreateCalls { get; private set; }
        public string? LastGetId { get; private set; }
        public string? LastGetTenant { get; private set; }
        public string? LastGetPrincipal { get; private set; }

        public NavigatorSession? Get(string id, string tenant, string principal)
        {
            LastGetId = id;
            LastGetTenant = tenant;
            LastGetPrincipal = principal;
            return ExistingSession;
        }

        public NavigatorSession Create(string tenant, string principal, string clientInstance, string routeId)
        {
            CreateCalls++;
            return CreatedSession!;
        }

        public bool AddPending(string id, NavigatorCommand command, string tenant, string principal, DateTimeOffset expiresAt) => false;
        public bool ApprovePending(string id, string commandId, string digest, string tenant, string principal, DateTimeOffset now) => false;
        public NavigatorCommand? ConsumeApproved(string id, string commandId, string tenant, string principal, DateTimeOffset now) => null;
        public CommandGrant? IssueGrant(string id, NavigatorCommand command, string tenant, string principal, NavigatorRegisteredTarget target, DateTimeOffset now) => null;
        public bool Execute(string id, NavigatorCommand command, CommandGrant grant, DateTimeOffset now) => false;
        public IReadOnlyList<NavigatorEvent> Events(string id, long after) => Array.Empty<NavigatorEvent>();
    }
}
