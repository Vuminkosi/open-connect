using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using OpenConnect.Abstractions;
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
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(token, "e2e-todo-internal-do-not-use-in-prod", StringComparison.Ordinal))
            throw new InvalidOperationException("TODO_INTERNAL_TOKEN must be replaced outside Development.");
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
            var tenantValid = false;
            try { tenantValid = Ulid.Parse(tenant) != default; } catch (FormatException) { }
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]),
                    Encoding.UTF8.GetBytes(options.Token)) ||
                !tenantValid)
                return Results.Unauthorized();
            return await next(context);
        });

        group.MapGet("", async (HttpContext context, ITenantTodoStore store, CancellationToken ct) => Results.Ok(await store.GetAllAsync(Tenant(context), ct)));
        group.MapPost("", async (HttpContext context, TodoCreateRequest request, ITenantTodoStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title of 1-200 characters is required."] });
            if (!Enum.TryParse<TodoPriority>(request.Priority, true, out var priority))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["priority"] = ["A valid Todo priority is required."] });
            return Results.Ok(await store.AddAsync(Tenant(context), request.Title.Trim(), request.Description?.Trim(), priority, request.DueDateUtc, ct));
        });
        group.MapPut("/{id:guid}", async (Guid id, HttpContext context, TodoUpdateRequest request, ITenantTodoStore store, CancellationToken ct) =>
            await store.UpdateStatusAsync(Tenant(context), id, request.IsCompleted, ct) ? Results.Ok() : Results.NotFound());
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, ITenantTodoStore store, CancellationToken ct) =>
            await store.DeleteAsync(Tenant(context), id, ct) ? Results.NoContent() : Results.NotFound());
        return endpoints;
    }

    private static TenantId Tenant(HttpContext context) => new(Ulid.Parse(context.Request.Headers["X-OpenConnect-Tenant"]!));
}

public sealed record TodoCreateRequest(string Title, string? Description = null, string Priority = "Medium", DateTime? DueDateUtc = null);
public sealed record TodoUpdateRequest(bool IsCompleted);
