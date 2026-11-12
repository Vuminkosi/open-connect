using System.Net.Http.Json;
using System.Text.Json;
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OpenAI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
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

    public static MetaCloudOptions FromConfiguration(IConfiguration configuration)
    {
        var endpoint = configuration["OpenConnect:ApiUrl"] ?? configuration["OpenConnect__ApiUrl"] ?? string.Empty;
        var apiKey = configuration["OpenConnect:ApiKey"] ?? configuration["OpenConnect__ApiKey"] ?? string.Empty;
        var model = configuration["OpenConnect:ReasoningModel"] ?? configuration["OpenConnect__ReasoningModel"] ?? string.Empty;
        var todoUrl = configuration["TODO_SERVICE_URL"] ?? string.Empty;
        var todoToken = configuration["TODO_INTERNAL_TOKEN"] ?? string.Empty;
        var tenant = configuration["TODO_INTERNAL_TENANT"] ?? string.Empty;
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
            AllowMutations = configuration.GetValue("TODO_ALLOW_MUTATIONS", false)
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

public sealed record TodoServiceRecord(Guid Id, string Title, string? Description, bool IsCompleted, string Priority, DateTime CreatedAtUtc, DateTime? DueDateUtc);

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

public sealed class MetaTodoTaskExecutor(MafRealAgentFactory factory, TodoServiceClient todo, IOptions<MetaCloudOptions> options) : IHostTaskExecutor
{
    private readonly MetaCloudOptions _options = options.Value;

    public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(async (CancellationToken ct) => await todo.ListAsync(ct).ConfigureAwait(false), "todo.list", "List the current tenant's Todo records.")
        };
        if (_options.AllowMutations)
        {
            tools.Add(AIFunctionFactory.Create(async (string title, string? description, string priority, DateTime? dueDateUtc, CancellationToken ct) => await todo.CreateAsync(title, description, priority, dueDateUtc, ct).ConfigureAwait(false), "todo.create", "Create a Todo for the current tenant."));
            tools.Add(AIFunctionFactory.Create(async (string todoId, CancellationToken ct) => await todo.CompleteAsync(Guid.Parse(todoId), ct).ConfigureAwait(false), "todo.complete", "Complete a Todo by id."));
            tools.Add(AIFunctionFactory.Create(async (string todoId, bool isCompleted, CancellationToken ct) => await todo.UpdateAsync(Guid.Parse(todoId), isCompleted, ct).ConfigureAwait(false), "todo.update", "Update Todo completion state."));
            tools.Add(AIFunctionFactory.Create(async (string todoId, CancellationToken ct) => await todo.DeleteAsync(Guid.Parse(todoId), ct).ConfigureAwait(false), "todo.delete", "Delete a Todo by id."));
        }

        var response = await factory.RunAsync(new AgentId("worker-sample-direct"), task.Message, tools, cancellationToken).ConfigureAwait(false);
        _ = response;
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
