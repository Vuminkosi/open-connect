using System.ClientModel;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Agents.Templates;
using OpenConnect.Core;
using OpenConnect.Docs.AssistantHost.Execution;
using OpenConnect.Docs.AssistantHost.Identity;
using OpenConnect.Docs.AssistantHost.LiveUI;
using OpenConnect.Docs.AssistantHost.Sessions;
using OpenConnect.Docs.AssistantHost.Speech;
using OpenConnect.Docs.Services;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Protection;
using OpenConnect.LiveUI.Hosting;
using OpenConnect.LiveUI.Inference;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("The process-local documentation assistant host requires Development.");

var tenant = builder.Configuration["OpenConnect:Assistant:VisitorTenant"];
var key = builder.Configuration["OpenConnect:Assistant:VisitorSigningKey"];
var endpoint = builder.Configuration["OpenConnect:Assistant:Model:Endpoint"];
var model = builder.Configuration["OpenConnect:Assistant:Model:Name"];
var apiKey = builder.Configuration["OpenConnect:Assistant:Model:ApiKey"];
if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model) ||
    string.IsNullOrWhiteSpace(apiKey) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var modelEndpoint) ||
    modelEndpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(modelEndpoint.UserInfo) ||
    !string.IsNullOrEmpty(modelEndpoint.Query) || !string.IsNullOrEmpty(modelEndpoint.Fragment))
    throw new InvalidOperationException("The documentation assistant host requires visitor identity and HTTPS model settings.");

builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens(tenant, key));
builder.Services.AddAuthentication(VisitorAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, VisitorAuthenticationHandler>(VisitorAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("documentation", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});
builder.Services.AddHttpClient("openconnect-kokoro", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddOpenConnect(options => options.Privacy.Enabled = true);
builder.Services.AddOpenConnectAgent("documentation-assistant");
builder.Services.AddOpenConnectTaskExecution(options => { options.Capacity = 32; options.MaxRetainedTasks = 2048; });
builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, DocumentationTaskAuthorizer>();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddOpenConnectNativeAgentSessionProtection();
builder.Services.AddSingleton<INativeAgentSessionStore, LocalNativeSessionStore>();
builder.Services.AddSingleton<INativeSessionTurnGate, LocalNativeTurnGate>();
builder.Services.AddSingleton<ILiveUIHostExecutionInputsFactory, DocumentationExecutionInputsFactory>();
builder.Services.AddSingleton<IChatClient>(_ => new ChatClient(model, new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = modelEndpoint }).AsIChatClient());
builder.Services.AddOpenConnectLiveUIHostExecution();
var workerEndpoint = builder.Configuration["OpenConnect:LiveUI:Inference:Worker:Endpoint"];
var workerToken = builder.Configuration["OpenConnect:LiveUI:Inference:Worker:Token"];
if (!string.IsNullOrWhiteSpace(workerEndpoint) || !string.IsNullOrWhiteSpace(workerToken))
{
    if (!Uri.TryCreate(workerEndpoint, UriKind.Absolute, out var workerUri) || string.IsNullOrWhiteSpace(workerToken) ||
        workerToken.Any(char.IsWhiteSpace) || workerToken.Any(char.IsControl))
        throw new InvalidOperationException("LiveUI worker configuration requires a private endpoint and token.");
    builder.Services.AddLiveUILocalWorkerInferenceProvider(options =>
    {
        options.Endpoint = workerUri;
        options.BearerToken = workerToken;
    }, registerRouterProvider: false);
}
builder.Services.AddScoped<IDocumentationWorkerPlanner, DocumentationWorkerPlanner>();
builder.Services.AddScoped<DocumentationLiveUIPlanner>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var address = context.Connection.RemoteIpAddress;
    if (!context.Request.IsHttps || address is null || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<SpeechPrivacyMiddleware>();
app.UseWebSockets();
app.MapOpenConnect();
app.MapDocumentationLiveUIInference();
app.Run();
