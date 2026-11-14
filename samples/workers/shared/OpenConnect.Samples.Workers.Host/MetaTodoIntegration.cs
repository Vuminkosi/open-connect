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

public sealed class MetaTodoTaskExecutor(MafRealAgentFactory factory, TodoServiceClient todo, InMemorySessionStore sessions, IOptions<MetaCloudOptions> options, ILogger<MetaTodoTaskExecutor> logger) : IHostTaskExecutor
{
    private readonly MetaCloudOptions _options = options.Value;

    public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        try
        {
            var tools = new List<AITool>();
            var coreRequest = RequiresTodoTools(task.Message);
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
                "You are the OpenConnect assistant. Handle ordinary conversation naturally. Use Todo tools only when the user explicitly asks to view or change Todo records. Never claim that a Todo was created, changed, completed, or deleted unless a tool confirms it.").ConfigureAwait(false);
            if (coreRequest)
            {
                await sessions.AppendEventAsync(task.SessionId.Value, "message", CompletionAcknowledgement(task.Message), cancellationToken).ConfigureAwait(false);
            }
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

    private static string InitialAcknowledgement(string message)
    {
        var value = message.ToLowerInvariant();
        if (value.Contains("delete") || value.Contains("remove")) return "I’ll remove that task for you. ";
        if (value.Contains("complete") || value.Contains("finish") || value.Contains("mark")) return "I’ll update that task’s status. ";
        if (value.Contains("create") || value.Contains("add") || value.Contains("remind")) return "I’ll add that reminder to your list. ";
        if (value.Contains("update") || value.Contains("change") || value.Contains("edit")) return "I’ll update that task for you. ";
        return "I’ll look through your tasks now. ";
    }

    private static string CompletionAcknowledgement(string message)
    {
        var value = message.ToLowerInvariant();
        if (value.Contains("delete") || value.Contains("remove")) return "That task has been removed. ";
        if (value.Contains("complete") || value.Contains("finish") || value.Contains("mark")) return "That task status has been updated. ";
        if (value.Contains("create") || value.Contains("add") || value.Contains("remind")) return "Your reminder request is ready. ";
        if (value.Contains("update") || value.Contains("change") || value.Contains("edit")) return "That task update is ready. ";
        return "I found the relevant tasks. ";
    }

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
