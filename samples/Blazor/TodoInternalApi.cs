using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor;

public sealed class TodoInternalOptions
{
    public string Token { get; init; } = string.Empty;
    public string AllowedTenant { get; init; } = string.Empty;

    public static TodoInternalOptions FromConfiguration(IConfiguration configuration)
    {
        var token = configuration["TODO_INTERNAL_TOKEN"] ??
            (string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase)
                ? "e2e-todo-internal-do-not-use-in-prod"
                : string.Empty);
        var tenant = configuration["TODO_INTERNAL_TENANT"] ?? "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(tenant))
            throw new InvalidOperationException("TODO_INTERNAL_TOKEN and TODO_INTERNAL_TENANT are required for the internal Todo API.");
        return new TodoInternalOptions { Token = token, AllowedTenant = tenant };
    }
}

public static class TodoInternalApiExtensions
{
    public static IEndpointRouteBuilder MapTodoInternalApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/internal/openconnect/todos");
        group.AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<TodoInternalOptions>();
            var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
            var tenant = context.HttpContext.Request.Headers["X-OpenConnect-Tenant"].ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]),
                    Encoding.UTF8.GetBytes(options.Token)) ||
                !string.Equals(tenant, options.AllowedTenant, StringComparison.Ordinal))
                return Results.Unauthorized();
            return await next(context);
        });

        group.MapGet("", async (ITodoStore store, CancellationToken ct) => Results.Ok(await store.GetAllAsync(ct)));
        group.MapPost("", async (TodoCreateRequest request, ITodoStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title of 1-200 characters is required."] });
            if (!Enum.TryParse<TodoPriority>(request.Priority, true, out var priority))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["priority"] = ["A valid Todo priority is required."] });
            return Results.Ok(await store.AddAsync(request.Title.Trim(), request.Description?.Trim(), priority, request.DueDateUtc, ct));
        });
        group.MapPut("/{id:guid}", async (Guid id, TodoUpdateRequest request, ITodoStore store, CancellationToken ct) =>
            await store.UpdateStatusAsync(id, request.IsCompleted, ct) ? Results.Ok(await store.GetByIdAsync(id, ct)) : Results.NotFound());
        group.MapDelete("/{id:guid}", async (Guid id, ITodoStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        return endpoints;
    }
}

public sealed record TodoCreateRequest(string Title, string? Description = null, string Priority = "Medium", DateTime? DueDateUtc = null);
public sealed record TodoUpdateRequest(bool IsCompleted);
