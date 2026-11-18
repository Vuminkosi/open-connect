using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OpenAI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI.Agents;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace OpenConnect.Samples.Workers.Host;

public sealed class MetaCloudOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string TodoServiceUrl { get; init; } = string.Empty;
    public string TodoInternalToken { get; init; } = string.Empty;
    public string TodoTenant { get; init; } = string.Empty;
    public string LocalGuardCredential { get; init; } = string.Empty;
    public bool AllowMutations { get; init; }
    public bool MutationApprovalGranted { get; init; }

    public static MetaCloudOptions FromConfiguration(IConfiguration configuration)
    {
        var endpoint = configuration["OpenConnect:ApiUrl"] ?? configuration["OpenConnect__ApiUrl"] ?? string.Empty;
        var apiKey = configuration["OpenConnect:ApiKey"] ?? configuration["OpenConnect__ApiKey"] ?? string.Empty;
        var model = configuration["OpenConnect:ReasoningModel"] ?? configuration["OpenConnect__ReasoningModel"] ?? string.Empty;
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        var todoUrl = configuration["TODO_SERVICE_URL"] ??
            (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase) ? "http://127.0.0.1:5000" : string.Empty);
        var todoToken = configuration["TODO_INTERNAL_TOKEN"] ?? string.Empty;
        var tenant = configuration["TODO_INTERNAL_TENANT"] ?? string.Empty;
        var guardEnabled = configuration.GetValue<bool>("OpenConnect:LocalAcceptance:Enabled");
        var guardCredential = guardEnabled ? configuration["OpenConnect:LocalAcceptance:ServiceBearer"] : null;
        if (guardEnabled && (guardCredential is null || guardCredential.Length != 43 ||
            guardCredential.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')))
            throw new InvalidOperationException("A valid local acceptance service bearer is required.");
        if (guardEnabled && (string.IsNullOrWhiteSpace(configuration["TODO_SERVICE_URL"]) || !IsLoopbackAuthority(todoUrl)))
            throw new InvalidOperationException("TODO_SERVICE_URL must be an exact loopback authority in local acceptance mode.");
        if (guardEnabled && string.Equals(guardCredential, todoToken, StringComparison.Ordinal))
            throw new InvalidOperationException("The local guard and Todo credentials must be distinct.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _)) throw new InvalidOperationException("OpenConnect:ApiUrl must be an absolute Meta-compatible endpoint.");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(todoUrl) || string.IsNullOrWhiteSpace(todoToken) || string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("Meta inference and internal Todo service configuration are required.");
        return new MetaCloudOptions
        {
            ApiKey = apiKey,
            Endpoint = endpoint,
            Model = model,
            TodoServiceUrl = todoUrl,
            TodoInternalToken = todoToken,
            TodoTenant = tenant,
            LocalGuardCredential = guardCredential ?? string.Empty,
            AllowMutations = configuration.GetValue("TODO_ALLOW_MUTATIONS", false),
            MutationApprovalGranted = string.Equals(configuration["TODO_MUTATION_APPROVAL"], "approved", StringComparison.Ordinal)
        };
    }
    internal static bool IsLoopbackAuthority(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) &&
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) &&
        string.Equals(value.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    internal bool IsConfiguredTenant(TenantId tenantId)
    {
        if (tenantId == default || string.IsNullOrWhiteSpace(TodoTenant))
            return false;

        try
        {
            var configuredTenant = new TenantId(Ulid.Parse(TodoTenant));
            return configuredTenant != default && tenantId == configuredTenant;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class TodoServiceClient(HttpClient httpClient, IOptions<MetaCloudOptions> options)
{
    private readonly MetaCloudOptions _options = options.Value;

    public async Task<IReadOnlyList<TodoServiceRecord>> ListAsync(CancellationToken cancellationToken)
        => await SendAsync<IReadOnlyList<TodoServiceRecord>>(HttpMethod.Get, "", null, cancellationToken).ConfigureAwait(false) ?? [];

    public Task<TodoServiceRecord?> CreateAsync(string title, string? description, string priority, DateTime? dueDateUtc, CancellationToken cancellationToken)
        => SendAsync<TodoServiceRecord>(HttpMethod.Post, "", new { title, description, priority, dueDateUtc }, cancellationToken);

    public Task<TodoServiceRecord?> CompleteAsync(Guid id, CancellationToken cancellationToken)
        => SendAsync<TodoServiceRecord>(HttpMethod.Put, id.ToString("D"), new { isCompleted = true }, cancellationToken);

    public Task<TodoServiceRecord?> UpdateAsync(Guid id, bool isCompleted, CancellationToken cancellationToken)
        => SendAsync<TodoServiceRecord>(HttpMethod.Put, id.ToString("D"), new { isCompleted }, cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendResponseAsync(HttpMethod.Delete, id.ToString("D"), null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string suffix, object? payload, CancellationToken cancellationToken)
    {
        using var response = await SendResponseAsync(method, suffix, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendResponseAsync(HttpMethod method, string suffix, object? payload, CancellationToken cancellationToken)
    {
        if (suffix.Length != 0 && !Guid.TryParseExact(suffix, "D", out _))
            throw new InvalidOperationException("Invalid Todo request path.");
        var baseAddress = httpClient.BaseAddress;
        if (_options.LocalGuardCredential.Length != 0 &&
            (_options.LocalGuardCredential.Length != 43 ||
             _options.LocalGuardCredential.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
             string.IsNullOrWhiteSpace(_options.TodoInternalToken) ||
             string.Equals(_options.LocalGuardCredential, _options.TodoInternalToken, StringComparison.Ordinal)))
            throw new InvalidOperationException("Invalid local Todo service credentials.");
        if (_options.LocalGuardCredential.Length != 0 &&
            (baseAddress is null || !MetaCloudOptions.IsLoopbackAuthority(_options.TodoServiceUrl) ||
             !string.Equals(baseAddress.GetLeftPart(UriPartial.Authority), new Uri(_options.TodoServiceUrl).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(baseAddress.AbsolutePath, "/internal/openconnect/todos/", StringComparison.Ordinal) ||
             baseAddress.Query.Length != 0 || baseAddress.Fragment.Length != 0 || baseAddress.UserInfo.Length != 0))
            throw new InvalidOperationException("Invalid local Todo service destination.");
        using var request = new HttpRequestMessage(method, suffix);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.TodoInternalToken);
        if (_options.LocalGuardCredential.Length != 0)
            request.Headers.Add("X-OpenConnect-Local-Guard", _options.LocalGuardCredential);
        request.Headers.Add("X-OpenConnect-Tenant", _options.TodoTenant);
        request.Headers.Add("X-OpenConnect-Principal", "gateway-workload");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        if (_options.LocalGuardCredential.Length != 0)
        {
            // A supplied HttpClient can hide an auto-redirecting handler. Never send
            // guarded credentials through a handler whose redirect policy is unknown.
            using var guardedClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false
            });
            request.RequestUri = new Uri(baseAddress!, suffix);
            return await guardedClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record TodoServiceRecord(Guid Id, string Title, string? Description, bool IsCompleted, int Priority, DateTime CreatedAtUtc, DateTime? DueDateUtc);

internal sealed class ConnectionBoundInteractionBroker(IClientInteractionBroker inner, string connectionId) : IClientInteractionBroker
{
    public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
        string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner.RequestAsync(sessionId, request with { ConnectionId = connectionId }, expectedResultKind, timeout, cancellationToken);
}

public sealed class SampleTodoTaskAuthorizer(IOptions<MetaCloudOptions> options) : IHostTaskExecutionAuthorizer
{
    public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.TenantId == default || task.PrincipalId == default) throw new UnauthorizedAccessException("Missing trusted task scope.");
        if (!options.Value.IsConfiguredTenant(task.TenantId))
            throw new UnauthorizedAccessException("This task is not authorized for the Todo service.");
        if (string.IsNullOrWhiteSpace(task.Message)) throw new UnauthorizedAccessException("Task message is empty.");
        return Task.CompletedTask;
    }
}

public sealed class MetaTodoTaskExecutor(IAgentDefinitionRegistry agentRegistry, IChatClient chatClient, IEgressGate egressGate,
    TodoServiceClient todo, InMemorySessionStore sessions, IClientInteractionBroker interactionBroker,
    IOptions<MetaCloudOptions> options, ILogger<MetaTodoTaskExecutor> logger) : IHostTaskExecutor
{
    private readonly MetaCloudOptions _options = options.Value;
    private readonly ConditionalWeakTable<OpenConnect.Hosting.AspNetCore.SessionRecord, ConversationState> _conversations = new();

    public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        if (!_options.IsConfiguredTenant(task.TenantId))
            throw new UnauthorizedAccessException("This task is not authorized for the Todo service.");

        ConversationState? conversation = null;
        if (task.Origin == HostTaskOrigin.MiniComposer)
        {
            var session = sessions.Get(task.SessionId.Value);
            if (session is null || session.TenantId != task.TenantId || session.PrincipalId != task.PrincipalId)
                throw new UnauthorizedAccessException("The interactive session is not authorized.");
            conversation = _conversations.GetValue(session, static _ => new ConversationState());
            await conversation.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var tools = new List<AITool>();
            var coreRequest = RequiresTodoTools(task.Message);
            var liveUIRequest = task.Origin == HostTaskOrigin.MiniComposer;
            if (liveUIRequest)
            {
                if (string.IsNullOrWhiteSpace(task.ClientConnectionId))
                    throw new InvalidOperationException("An interactive task requires its original client connection.");
                tools.AddRange(LiveUIClientAgentToolFactory.CreateTools(
                    new ConnectionBoundInteractionBroker(interactionBroker, task.ClientConnectionId), task.SessionId));
            }
            if (coreRequest)
            {
                await sessions.AppendTaskEventAsync(task.SessionId.Value, task.TaskId, "message", "I’ll check your request. ", cancellationToken).ConfigureAwait(false);
                tools.Add(AIFunctionFactory.Create((CancellationToken ct) => RunToolAsync("checking your Todo list", todo.ListAsync, task, ct), "todo.list", "List the current tenant's Todo records."));
            }
            if (_options.AllowMutations && _options.MutationApprovalGranted && coreRequest)
            {
                tools.Add(AIFunctionFactory.Create((string title, string? description, string priority, DateTime? dueDateUtc, CancellationToken ct) => RunToolAsync("creating that Todo", innerCt => todo.CreateAsync(title, description, priority, dueDateUtc, innerCt), task, ct), "todo.create", "Create a Todo for the current tenant."));
                tools.Add(AIFunctionFactory.Create((string todoId, CancellationToken ct) => RunToolAsync("completing that Todo", innerCt => todo.CompleteAsync(Guid.Parse(todoId), innerCt), task, ct), "todo.complete", "Complete a Todo by id."));
                tools.Add(AIFunctionFactory.Create((string todoId, bool isCompleted, CancellationToken ct) => RunToolAsync("updating that Todo", innerCt => todo.UpdateAsync(Guid.Parse(todoId), isCompleted, innerCt), task, ct), "todo.update", "Update Todo completion state."));
                tools.Add(AIFunctionFactory.Create((string todoId, CancellationToken ct) => RunToolAsync("deleting that Todo", innerCt => todo.DeleteAsync(Guid.Parse(todoId), innerCt), task, ct), "todo.delete", "Delete a Todo by id."));
            }

            var egressContext = new EgressContext(task.TenantId, task.PrincipalId, task.SessionId, EgressDestination.ExternalModel);
            var factory = new MafRealAgentFactory(agentRegistry, chatClient, egressGate,
                new FixedEgressContextProvider(egressContext));
            var messages = conversation?.Messages.ToList() ?? [];
            messages.Add(new ChatMessage(ChatRole.User, task.Message));
            var response = await factory.RunConversationAsync(
                new AgentId("worker-sample-direct"),
                messages,
                tools,
                cancellationToken,
                AgentInstructions).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                await sessions.AppendTaskEventAsync(task.SessionId.Value, task.TaskId, "message", response.Text, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                conversation?.Append(task.Message, response.Text);
            }
            else
            {
                conversation?.Messages.Clear();
            }
        }
        catch (Exception exception)
        {
            conversation?.Messages.Clear();
            logger.LogError(exception, "Meta Todo task execution failed for task {TaskId}.", task.TaskId.Value);
            throw;
        }
        finally
        {
            conversation?.Gate.Release();
        }
    }

    private sealed class ConversationState
    {
        private const int MaxTurns = 4;
        private const int MaxMessageLength = 4000;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<ChatMessage> Messages { get; } = [];

        public void Append(string user, string assistant)
        {
            if (user.Length > MaxMessageLength || assistant.Length > MaxMessageLength)
            {
                Messages.Clear();
                return;
            }
            Messages.Add(new ChatMessage(ChatRole.User, user));
            Messages.Add(new ChatMessage(ChatRole.Assistant, assistant));
            while (Messages.Count > MaxTurns * 2) Messages.RemoveRange(0, 2);
        }
    }

    private const string AgentInstructions = """
        You are the OpenConnect assistant. Handle ordinary conversation naturally on the server; do not call LiveUI or browser-inference tools for non-UI requests. The runtime may already have sent an acknowledgement, so do not repeat it or add a greeting; begin with the requested result. When the user asks for a tour, introduction, or walkthrough, make it interactive: perform only the first relevant UI action, explain that area while narrating, and ask whether the user wants to continue. Do not replace an interactive walkthrough with a static feature overview. When the user continues a tour, compare the proposed area with the area you explained in the preceding turn. If the plan proposes that same area, do not repeat it: get the current page and choose a different authorized target to highlight or navigate to. If there is no suitable different target, say that you cannot advance; do not claim you moved to another area. An explicit request to revisit an area may repeat it.

        For every request that can be fulfilled by navigating, filtering, searching, selecting, inspecting, or highlighting the visible application, perform the corresponding LiveUI interaction. Do not answer a request to show or find a task with todo.list text alone. If a request needs task data (for example, identifying the most recently created task or answering which tasks are completed), use todo.list as a read-only source, then use the existing UI to display that exact result. Select the All tab first if a status filter could hide the result, and use the existing search input to reveal a specific task. For completed-task questions, select and verify the Completed tab, then focus each matching visible row as its title is spoken.

        After any required read-only record lookup, call liveui_infer_plan before a UI action. Use only its validated targets and actions, except when a tour continuation plan repeats the preceding area: in that case get the current page and use a different exact target and capability returned there. When a valid plan is returned, act directly on its targets; do not call liveui_get_current_page just to repeat the target catalogue. If no plan is returned because browser and worker inference are unavailable, call liveui_get_current_page and use only the exact targets and capabilities it returns. Execute actions through the LiveUI tools and wait for each completed, postcondition-verified receipt before dependent actions. SelectTab already waits for and verifies the selected state, so do not follow a successful SelectTab receipt with a redundant liveui_await_state call. Use liveui_await_state when the preceding receipt does not verify the required asynchronous state, such as a filtered list after SetValue. The server owns routing, authorization, execution, and verification. Never invent a target or claim a change without a completed, postcondition-verified receipt.

        After the UI action is verified, give a concise spoken explanation of what is now visible. When narration is enabled, highlight the relevant region or exact task row while the response is read aloud. For a task row, pass its exact visible title as matchText. Supply speechStartCharacter and speechEndCharacter as the zero-based UTF-16 range of that exact title in your final response so the highlight follows the spoken segment. Claim a highlight only after a completed, verified receipt; report failures plainly.

        If the user asks to highlight task status without naming a task, do not stop to ask for a row title. Use the current page and validated plan to highlight the aggregate completion/progress region for overall status, or the Todo list region for status controls. If a requested row cannot be uniquely grounded from the current conversation or read-only Todo records, highlight the enclosing Todo list region and explain that it is a region highlight; never claim a specific row was highlighted. After a failed row highlight, do not repeat the same row action; fall back to the authorized aggregate or list region only when that satisfies the request, and verify its receipt before responding.

        When the user says a highlight did not come through, asks you to retry it, or describes a visible area that can be highlighted, treat that as an instruction to perform the highlight now. Resolve "it" from the immediately preceding request and current page. If the prior request identifies a task but its exact title is unavailable, highlight the enclosing Todo list region as a useful fallback; do not ask the user to choose between a title and an area when the visible region is already known. Call liveui_highlight on the registered target and wait for its verified receipt before saying the highlight succeeded. If the target is unavailable or the action fails, state that clearly and offer the nearest valid target only after attempting the requested action.

        For requests to explain the current view, call liveui_infer_plan first, then call liveui_inspect on the target in the validated plan and use its privacy-safe receipt. You may call liveui_get_current_page to learn the labels and controls; if planning is unavailable, select an authorized inspect target from that page. For navigation, call liveui_infer_plan before liveui_navigate, including when the user names the destination. Navigate to the route in the validated plan, then get the current page again before acting on a target. If planning is unavailable, use only routes returned by liveui_get_current_page. Use SelectTab for tabs, SetValue for text inputs, and Select for selection controls. Click alone has no verified postcondition.

        For a task recommendation, use todo.list as read-only data. Exclude completed records and recommend only a unique highest-priority remaining task (Urgent=3, High=2, Medium=1, Low=0); ask for clarification if there is no unique choice. Never create, update, complete, or delete a Todo unless the user explicitly requests that mutation, and never claim one changed without tool confirmation.
        """;

    private async Task<T> RunToolAsync<T>(string description, Func<CancellationToken, Task<T>> operation, HostTaskExecution task, CancellationToken cancellationToken)
    {
        var operationTask = operation(cancellationToken);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        if (await Task.WhenAny(operationTask, delayTask).ConfigureAwait(false) == delayTask)
        {
            await sessions.AppendTaskEventAsync(task.SessionId.Value, task.TaskId, "message", $"This is taking a little longer while I’m {description}. ", cancellationToken).ConfigureAwait(false);
        }
        return await operationTask.ConfigureAwait(false);
    }

    private static bool RequiresTodoTools(string message)
    {
        var value = message.Trim().ToLowerInvariant();
        string[] terms = ["todo", "to-do", "task", "tasks", "reminder", "remind me"];
        return terms.Any(value.Contains);
    }
}

public static class MetaCloudRegistration
{
    public static IServiceCollection AddMetaTodoIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var options = MetaCloudOptions.FromConfiguration(configuration);
        services.AddOpenConnectRulesDetector();
        services.AddSingleton(Options.Create(options));
        services.AddHttpClient<TodoServiceClient>(client => client.BaseAddress = new Uri(options.TodoServiceUrl.TrimEnd('/') + "/internal/openconnect/todos/"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IChatClient>(_ =>
        {
            var chat = new ChatClient(options.Model, new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
            return chat.AsIChatClient();
        });
        services.AddSingleton<IHostTaskExecutionAuthorizer, SampleTodoTaskAuthorizer>();
        services.AddSingleton<IHostTaskExecutor, MetaTodoTaskExecutor>();
        return services;
    }
}
