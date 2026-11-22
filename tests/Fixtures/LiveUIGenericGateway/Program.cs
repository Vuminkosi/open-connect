using System.ClientModel;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Agents.Templates;
using OpenConnect.Core;
using OpenConnect.Docs.Services;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Protection;
using OpenConnect.LiveUI.Hosting;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Samples.Workers.Host;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment() || builder.Configuration["LIVEUI_GENERIC_FIXTURE"] != "1")
    throw new InvalidOperationException("The generic gateway fixture requires explicit development opt-in.");
var token = builder.Configuration["OC_LOCAL_SERVICE_AUTH_SECRET"];
var tenant = builder.Configuration["OC_LOCAL_SERVICE_AUTH_TENANT_ID"];
var principal = builder.Configuration["OC_LOCAL_SERVICE_AUTH_PRINCIPAL_ID"];
var apiKey = builder.Configuration["OpenConnect:ApiKey"];
var apiUrl = builder.Configuration["OpenConnect:ApiUrl"];
var model = builder.Configuration["OpenConnect:ReasoningModel"];
if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(principal) ||
    string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model) || !Uri.TryCreate(apiUrl, UriKind.Absolute, out var endpoint))
    throw new InvalidOperationException("The fixture requires authentication and model configuration.");
_ = Ulid.Parse(tenant);
_ = Ulid.Parse(principal);
var visitorKey = builder.Configuration["OC_DOCS_ASSISTANT_VISITOR_KEY"];
var visitors = string.IsNullOrWhiteSpace(visitorKey) ? null : new DocumentationGatewayVisitorTokens(tenant, visitorKey);
builder.Services.AddSingleton(new FixtureIdentity(token, tenant, principal, visitors));
builder.Services.AddAuthentication(FixtureAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, FixtureAuth>(FixtureAuth.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("openconnect-kokoro", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("liveui-inference", limiter =>
{
    limiter.PermitLimit = 20;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));
builder.Services.AddOpenConnect(options => options.Privacy.Enabled = true);
builder.Services.AddOpenConnectAgent("generic-liveui");
builder.Services.AddOpenConnectTaskExecution();
builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, FixtureAuthorizer>();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddOpenConnectNativeAgentSessionProtection();
builder.Services.AddSingleton<INativeAgentSessionStore, FixtureSessionStore>();
builder.Services.AddSingleton<INativeSessionTurnGate, FixtureTurnGate>();
builder.Services.AddSingleton<ILiveUIHostExecutionInputsFactory, FixtureInputsFactory>();
var blockModel = builder.Configuration["LIVEUI_GENERIC_BLOCK_MODEL"] == "1";
builder.Services.AddSingleton<IChatClient>(_ => blockModel ? new FixtureBlockingChatClient() :
    new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint }).AsIChatClient());
builder.Services.AddOpenConnectLiveUIHostExecution();
var worker = builder.Configuration["OpenConnect:LiveUI:Inference:Worker:Endpoint"];
var workerToken = builder.Configuration["OpenConnect:LiveUI:Inference:Worker:Token"];
if (!string.IsNullOrWhiteSpace(worker) && !string.IsNullOrWhiteSpace(workerToken))
    builder.Services.AddLiveUILocalWorkerInferenceProvider(options => { options.Endpoint = new Uri(worker); options.BearerToken = workerToken; });
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets();
app.MapOpenConnect();
app.MapOpenConnectLiveUIInference();
app.Run();

internal sealed record FixtureIdentity(string Token, string Tenant, string Principal, DocumentationGatewayVisitorTokens? Visitors);

internal sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, FixtureIdentity identity) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LiveUIGenericFixture";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (identity.Visitors is not null)
        {
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !identity.Visitors.TryValidate(header[7..], out var tenant, out var principal))
                return Task.FromResult(AuthenticateResult.NoResult());
            var visitorClaims = new[] { new Claim("tenant_id", tenant), new Claim(ClaimTypes.NameIdentifier, principal) };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(visitorClaims, SchemeName)), SchemeName)));
        }
        var expected = Encoding.UTF8.GetBytes("Bearer " + identity.Token);
        var actual = Encoding.UTF8.GetBytes(header);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[] { new Claim("tenant_id", identity.Tenant), new Claim(ClaimTypes.NameIdentifier, identity.Principal) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

internal sealed class FixtureAuthorizer : IHostTaskExecutionAuthorizer
{
    public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.TenantId == default || task.PrincipalId == default) throw new UnauthorizedAccessException();
        return Task.CompletedTask;
    }
}

internal sealed class FixtureInputsFactory(IAgentDefinitionRegistry registry, IChatClient client, IEgressGate gate, IConfiguration configuration,
    INativeAgentSessionStore store, INativeAgentSessionProtector protector, INativeSessionTurnGate turnGate) : ILiveUIHostExecutionInputsFactory
{
    public Task<LiveUIHostExecutionInputs> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        var binding = new NativeAgentSessionBinding(task.SessionId, task.TenantId, task.PrincipalId,
            "generic-liveui", "fixture-v1", "maf", "v1", "configured-provider");
        var context = new AgentExecutionContext
        {
            TenantScope = task.TenantId.Value.ToString(), PrincipalOrWorkloadScope = task.PrincipalId.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser, SessionId = task.SessionId, TaskId = task.TaskId,
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1, PolicyRevision = 1,
            AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddSeconds(configuration["LIVEUI_GENERIC_BLOCK_MODEL"] == "1" ? 5 : 180), RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = cancellationToken
        };
        var egress = new EgressContext(task.TenantId, task.PrincipalId, task.SessionId, EgressDestination.ExternalModel);
        var factory = new MafRealAgentFactory(registry, client, gate, new FixedEgressContextProvider(egress));
        var persistence = new NativeSessionTurnPersistenceContext(new NativeAgentSessionPersistence(store, protector), binding, turnGate);
        return Task.FromResult(new LiveUIHostExecutionInputs(factory, new AgentId("generic-liveui"), context,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(false); }, persistence,
            configuration["LIVEUI_GENERIC_INSTRUCTIONS"]));
    }
}

internal sealed class FixtureBlockingChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("blocking-test-model");
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The blocking test model must be cancelled.");
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await GetResponseAsync(messages, options, cancellationToken);
        yield break;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class FixtureSessionStore : INativeAgentSessionStore
{
    private readonly Dictionary<NativeAgentSessionBinding, NativeAgentSessionRecord> _records = [];
    public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_records) return Task.FromResult(_records.GetValueOrDefault(binding)); }
    public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_records)
        {
            if (expectedVersion < 0 || expectedVersion == long.MaxValue || record.Version != expectedVersion + 1 ||
                (_records.GetValueOrDefault(record.Binding)?.Version ?? 0) != expectedVersion)
                return Task.FromResult(NativeAgentSessionSaveOutcome.Conflict);
            _records[record.Binding] = record;
            return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
        }
    }
}

internal sealed class FixtureTurnGate : INativeSessionTurnGate
{
    private readonly ConcurrentDictionary<NativeAgentSessionBinding, SemaphoreSlim> _gates = new();
    public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding, AgentExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding.SessionId != context.SessionId || binding.TenantId.Value.ToString() != context.TenantScope ||
            binding.PrincipalId.Value.ToString() != context.PrincipalOrWorkloadScope) return Task.FromResult<INativeSessionTurnOwnership?>(null);
        var gate = _gates.GetOrAdd(binding, _ => new SemaphoreSlim(1, 1));
        return Task.FromResult<INativeSessionTurnOwnership?>(gate.Wait(0) ? new Ownership(gate) : null);
    }
    private sealed class Ownership(SemaphoreSlim gate) : INativeSessionTurnOwnership
    {
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
