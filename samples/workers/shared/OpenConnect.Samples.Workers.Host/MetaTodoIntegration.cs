using System.Net.Http.Json;
using System.Text.Json;
using System.ClientModel;
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

namespace OpenConnect.Samples.Workers.Host;

public sealed class MetaCloudOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string TodoServiceUrl { get; init; } = string.Empty;
    public string TodoInternalToken { get; init; } = string.Empty;
    public string TodoTenant { get; init; } = string.Empty;
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
        var todoToken = configuration["TODO_INTERNAL_TOKEN"] ??
            (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase) ? "e2e-todo-internal-do-not-use-in-prod" : string.Empty);
        var tenant = configuration["TODO_INTERNAL_TENANT"] ?? "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(todoToken, "e2e-todo-internal-do-not-use-in-prod", StringComparison.Ordinal))
            throw new InvalidOperationException("TODO_INTERNAL_TOKEN must be replaced outside Development.");
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
            AllowMutations = configuration.GetValue("TODO_ALLOW_MUTATIONS", false),
            MutationApprovalGranted = string.Equals(configuration["TODO_MUTATION_APPROVAL"], "approved", StringComparison.Ordinal)
        };
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
        using var request = new HttpRequestMessage(method, suffix);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.TodoInternalToken);
        request.Headers.Add("X-OpenConnect-Tenant", _options.TodoTenant);
        request.Headers.Add("X-OpenConnect-Principal", "gateway-workload");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record TodoServiceRecord(Guid Id, string Title, string? Description, bool IsCompleted, int Priority, DateTime CreatedAtUtc, DateTime? DueDateUtc);

public sealed class SampleTodoTaskAuthorizer : IHostTaskExecutionAuthorizer
{
    public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.TenantId == default || task.PrincipalId == default) throw new UnauthorizedAccessException("Missing trusted task scope.");
        if (string.IsNullOrWhiteSpace(task.Message)) throw new UnauthorizedAccessException("Task message is empty.");
        return Task.CompletedTask;
    }
}

public sealed class MetaTodoTaskExecutor(IAgentDefinitionRegistry agentRegistry, IChatClient chatClient, IEgressGate egressGate,
    TodoServiceClient todo, InMemorySessionStore sessions, IClientInteractionBroker interactionBroker,
    IOptions<MetaCloudOptions> options, ILogger<MetaTodoTaskExecutor> logger) : IHostTaskExecutor
{
    private readonly MetaCloudOptions _options = options.Value;

    public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        try
        {
            var tools = new List<AITool>();
            var coreRequest = RequiresTodoTools(task.Message);
            var navigatorRequest = RequiresNavigatorTools(task.Message);
            var liveUIRequest = RequiresLiveUI(task.Message);
            if (liveUIRequest)
            {
                tools.AddRange(LiveUIClientAgentToolFactory.CreateTools(interactionBroker, task.SessionId));
            }
            if (navigatorRequest && !liveUIRequest)
            {
                tools.Add(AIFunctionFactory.Create((string route, CancellationToken ct) => EmitNavigatorActionAsync("navigate", route, null, task, ct), "navigator.navigate", "Navigate to an internal application route."));
                tools.Add(AIFunctionFactory.Create((string targetId, CancellationToken ct) => EmitNavigatorActionAsync("highlight", null, targetId, task, ct), "navigator.highlight", "Highlight and scroll to a registered page target."));
                tools.Add(AIFunctionFactory.Create((string targetId, CancellationToken ct) => EmitNavigatorActionAsync("focus", null, targetId, task, ct), "navigator.focus", "Focus a registered page target."));
                tools.Add(AIFunctionFactory.Create((string targetId, CancellationToken ct) => EmitNavigatorActionAsync("scroll", null, targetId, task, ct), "navigator.scroll", "Scroll to a registered page target."));
            }
            if (coreRequest)
            {
                await sessions.AppendEventAsync(task.SessionId.Value, "message", InitialAcknowledgement(task.Message), cancellationToken).ConfigureAwait(false);
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
            var response = await factory.RunAsync(
                new AgentId("worker-sample-direct"),
                task.Message,
                tools,
                cancellationToken,
                AgentInstructions).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                await sessions.AppendEventAsync(task.SessionId.Value, "message", response.Text, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Meta Todo task execution failed for task {TaskId}.", task.TaskId.Value);
            throw;
        }
    }

    private async Task<string> EmitNavigatorActionAsync(string action, string? route, string? targetId, HostTaskExecution task, CancellationToken cancellationToken)
    {
        if (action == "navigate" && (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/') || route.StartsWith("//") || route.Contains("..", StringComparison.Ordinal)))
            throw new ArgumentException("Only internal application routes are allowed.", nameof(route));
        if (action != "navigate" && (string.IsNullOrWhiteSpace(targetId) || !System.Text.RegularExpressions.Regex.IsMatch(targetId, "^[a-zA-Z0-9_-]{1,80}$")))
            throw new ArgumentException("A registered target id is required.", nameof(targetId));
        await sessions.AppendEventAsync(task.SessionId.Value, "navigator.action", JsonSerializer.Serialize(new { action, route, targetId }), cancellationToken).ConfigureAwait(false);
        return $"Navigator action '{action}' was sent to the Todo sample.";
    }

    private const string AgentInstructions = """
        You are the OpenConnect assistant. Handle ordinary conversation naturally on the server; do not call LiveUI or browser-inference tools for non-UI requests. The runtime may already have sent an acknowledgement, so do not repeat it or add a greeting; begin with the requested result. When the user asks for a tour, introduction, or walkthrough, make it interactive: perform only the first relevant UI action, explain that area while narrating, and ask whether the user wants to continue. Do not replace an interactive walkthrough with a static feature overview.

        For every request that can be fulfilled by navigating, filtering, searching, selecting, inspecting, or highlighting the visible application, perform the corresponding LiveUI interaction. Do not answer a request to show or find a task with todo.list text alone. If a request needs task data (for example, identifying the most recently created task or answering which tasks are completed), use todo.list as a read-only source, then use the existing UI to display that exact result. Select the All tab first if a status filter could hide the result, and use the existing search input to reveal a specific task. For completed-task questions, select and verify the Completed tab, then focus each matching visible row as its title is spoken.

        After any required read-only record lookup, call liveui_infer_plan before a UI action. Use only its validated targets and actions. When a valid plan is returned, act directly on its targets; do not call liveui_get_current_page just to repeat the target catalogue. If no plan is returned because browser and worker inference are unavailable, call liveui_get_current_page and use only the exact targets and capabilities it returns. Execute actions through the LiveUI tools and wait for each completed, postcondition-verified receipt before dependent actions. SelectTab already waits for and verifies the selected state, so do not follow a successful SelectTab receipt with a redundant liveui_await_state call. Use liveui_await_state when the preceding receipt does not verify the required asynchronous state, such as a filtered list after SetValue. The server owns routing, authorization, execution, and verification. Never invent a target or claim a change without a completed, postcondition-verified receipt.

        After the UI action is verified, give a concise spoken explanation of what is now visible. When narration is enabled, highlight the relevant region or exact task row while the response is read aloud. For a task row, pass its exact visible title as matchText. Supply speechStartCharacter and speechEndCharacter as the zero-based UTF-16 range of that exact title in your final response so the highlight follows the spoken segment. Claim a highlight only after a completed, verified receipt; report failures plainly.

        If the user asks to highlight task status without naming a task, do not stop to ask for a row title. Use the current page and validated plan to highlight the aggregate completion/progress region for overall status, or the Todo list region for status controls. If a requested row cannot be uniquely grounded from the current conversation or read-only Todo records, highlight the enclosing Todo list region and explain that it is a region highlight; never claim a specific row was highlighted. After a failed row highlight, do not repeat the same row action; fall back to the authorized aggregate or list region only when that satisfies the request, and verify its receipt before responding.

        When the user says a highlight did not come through, asks you to retry it, or describes a visible area that can be highlighted, treat that as an instruction to perform the highlight now. Resolve "it" from the immediately preceding request and current page. If the prior request identifies a task but its exact title is unavailable, highlight the enclosing Todo list region as a useful fallback; do not ask the user to choose between a title and an area when the visible region is already known. Call liveui_highlight on the registered target and wait for its verified receipt before saying the highlight succeeded. If the target is unavailable or the action fails, state that clearly and offer the nearest valid target only after attempting the requested action.

        For requests to explain the current view, use liveui_get_current_page and liveui_inspect for relevant privacy-safe state. For navigation, use only routes from the current manifest, call liveui_navigate, and get the current page again before acting on a target. Use SelectTab for tabs, SetValue for text inputs, and Select for selection controls. Click alone has no verified postcondition.

        For a task recommendation, use todo.list as read-only data. Exclude completed records and recommend only a unique highest-priority remaining task (Urgent=3, High=2, Medium=1, Low=0); ask for clarification if there is no unique choice. Never create, update, complete, or delete a Todo unless the user explicitly requests that mutation, and never claim one changed without tool confirmation.
        """;

    private static bool RequiresNavigatorTools(string message)
    {
        var value = message.ToLowerInvariant();
        return value.Contains("navigate") || value.Contains("go to") || value.Contains("take me") || value.Contains("highlight") || value.Contains("focus") || value.Contains("scroll") || value.Contains("show me where") || value.Contains("tour") || value.Contains("walk me through") || value.Contains("walk me around") || value.Contains("show me around") || value.Contains("introduce me") || value.Contains("guide me");
    }

    private static bool RequiresLiveUI(string message)
    {
        var value = message.ToLowerInvariant();
        string[] terms = ["completed tasks", "which task is completed", "which tasks are completed", "what task is completed", "what tasks are completed", "active tasks", "show all tasks", "pull up", "show my", "view my", "list my", "recent tasks", "recent todos", "most recent", "latest task", "last task", "my tasks", "my todos", "filter", "search", "find tasks", "highlight", "focus", "scroll", "navigate", "go to", "take me", "open page", "explain what", "what am i looking at", "todo list", "task list"];
        return terms.Any(value.Contains);
    }

    private static string InitialAcknowledgement(string message)
    {
        var value = message.ToLowerInvariant();
        if (ContainsIntent(value, "delete", "remove")) return "I’ll remove that task for you. ";
        if (ContainsIntent(value, "complete", "finish", "mark")) return "I’ll update that task’s status. ";
        if (ContainsIntent(value, "create", "add", "remind")) return "I’ll add that reminder to your list. ";
        if (ContainsIntent(value, "update", "change", "edit")) return "I’ll update that task for you. ";
        return "I’ll look through your tasks now. ";
    }

    private static bool ContainsIntent(string message, params string[] intents) =>
        intents.Any(intent => System.Text.RegularExpressions.Regex.IsMatch(
            message,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(intent)}\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    private async Task<T> RunToolAsync<T>(string description, Func<CancellationToken, Task<T>> operation, HostTaskExecution task, CancellationToken cancellationToken)
    {
        var operationTask = operation(cancellationToken);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        if (await Task.WhenAny(operationTask, delayTask).ConfigureAwait(false) == delayTask)
        {
            await sessions.AppendEventAsync(task.SessionId.Value, "message", $"This is taking a little longer while I’m {description}. ", cancellationToken).ConfigureAwait(false);
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
        services.AddHttpClient<TodoServiceClient>(client => client.BaseAddress = new Uri(options.TodoServiceUrl.TrimEnd('/') + "/internal/openconnect/todos/"));
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
