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

public sealed class MetaTodoTaskExecutor(MafRealAgentFactory factory, TodoServiceClient todo, InMemorySessionStore sessions,
    IClientInteractionBroker interactionBroker, IOptions<MetaCloudOptions> options, ILogger<MetaTodoTaskExecutor> logger) : IHostTaskExecutor
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

            var response = await factory.RunAsync(
                new AgentId("worker-sample-direct"),
                task.Message,
                tools,
                cancellationToken,
                "You are the OpenConnect assistant. Handle ordinary conversation naturally. The runtime may already emit an acknowledgement before your response, so do not repeat that acknowledgement or add a greeting; begin directly with the requested result. When the user asks for a tour, introduction, or to be walked through the app, make it interactive: perform only the first relevant action, explain that one area briefly, and ask whether they want to continue. Do not provide a static feature overview or list every step at once. For UI navigation, filtering, search, highlighting, or explanation requests, call liveui_get_current_page first and use only its returned target IDs and capabilities. Use liveui_navigate for manifest routes, then call liveui_get_current_page again before any target action. For tabs, use liveui_execute with action=SelectTab, never Click; for text inputs use action=SetValue, and for selection controls use action=Select. Click has no verified postcondition by itself. When the user requests a highlight, you must call liveui_highlight with a target ID from the current-page result; for a task row use the exact visible title as matchText. Do not claim a highlight succeeded unless that tool returns outcome=Completed and postconditionVerified=true; if it fails, report that. Use liveui_await_state with stateName=selected after changing a status tab and stateName=textContains/stateValue=<visible result text> on the stable todo-list region after searching, including the empty-state message when no tasks match, liveui_inspect for privacy-safe target state, and liveui_clear_highlight for popup-free guidance. Wait for each verified receipt before dependent actions or claims. To recommend a task or answer which task should be worked on, use todo.list as read-only source data. Priority values are TodoPriority enum values: Urgent=3, High=2, Medium=1, Low=0. Exclude records with IsCompleted=true and recommend only a unique highest-priority remaining record; if none exists or multiple records tie for highest priority, ask the user to clarify. Explain the selection criterion. When asked to highlight that recommendation, use its exact visible title as matchText on the authorized Task list target; claim success only after a verified receipt. Use Todo tools for other viewing or changes only when explicitly requested. Never mutate Todo data without an explicit request, and never claim a Todo was created, changed, completed, or deleted unless a tool confirms it.").ConfigureAwait(false);
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

    private static bool RequiresNavigatorTools(string message)
    {
        var value = message.ToLowerInvariant();
        return value.Contains("navigate") || value.Contains("go to") || value.Contains("take me") || value.Contains("highlight") || value.Contains("focus") || value.Contains("scroll") || value.Contains("show me where") || value.Contains("tour") || value.Contains("walk me through") || value.Contains("walk me around") || value.Contains("show me around") || value.Contains("introduce me") || value.Contains("guide me");
    }

    private static bool RequiresLiveUI(string message)
    {
        var value = message.ToLowerInvariant();
        string[] terms = ["completed tasks", "active tasks", "show all tasks", "filter", "search", "find tasks", "highlight", "focus", "scroll", "navigate", "go to", "take me", "open page", "explain what", "what am i looking at", "todo list", "task list"];
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
        services.AddSingleton(Options.Create(options));
        services.AddHttpClient<TodoServiceClient>(client => client.BaseAddress = new Uri(options.TodoServiceUrl.TrimEnd('/') + "/internal/openconnect/todos/"));
        services.AddSingleton<IChatClient>(_ =>
        {
            var chat = new ChatClient(options.Model, new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
            return chat.AsIChatClient();
        });
        services.AddSingleton<MafRealAgentFactory>();
        services.AddSingleton<IHostTaskExecutionAuthorizer, SampleTodoTaskAuthorizer>();
        services.AddSingleton<IHostTaskExecutor, MetaTodoTaskExecutor>();
        return services;
    }
}
