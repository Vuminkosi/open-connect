using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Tools;

/// <summary>Server-side policy decision for a single tool invocation.</summary>
public interface IToolAccessPolicy
{
    ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken);
}

/// <summary>The principal and resource scope evaluated for every discovery or invocation.</summary>
public sealed record ToolAccessRequest(
    AgentId AgentId,
    ToolId ToolId,
    PrincipalId PrincipalId,
    TenantId TenantId,
    TaskId TaskId);

/// <summary>A server-issued, single-use authorization bound to exact canonical arguments and identity scope.</summary>
public sealed class ToolApproval
{
    internal ToolApproval(string id, AgentId agentId, ToolId toolId, PrincipalId principalId,
        TenantId tenantId, TaskId taskId, string argumentsHash, DateTimeOffset expiresAt)
    {
        Id = id; AgentId = agentId; ToolId = toolId; PrincipalId = principalId;
        TenantId = tenantId; TaskId = taskId; ArgumentsHash = argumentsHash; ExpiresAt = expiresAt;
    }

    public string Id { get; }
    public AgentId AgentId { get; }
    public ToolId ToolId { get; }
    public PrincipalId PrincipalId { get; }
    public TenantId TenantId { get; }
    public TaskId TaskId { get; }
    public string ArgumentsHash { get; }
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>Provides policy filtering and one-time approval validation for tool use.</summary>
public sealed class ToolAuthorizationService(IToolAccessPolicy policy, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, byte> _consumedApprovals = new(StringComparer.Ordinal);

    public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken = default) =>
        policy.IsAllowedAsync(request, cancellationToken);

    public ToolApproval CreateApproval(ToolAccessRequest request, JsonElement arguments, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        return new ToolApproval(Guid.NewGuid().ToString("N"), request.AgentId, request.ToolId,
            request.PrincipalId, request.TenantId, request.TaskId, HashArguments(arguments), _timeProvider.GetUtcNow() + lifetime);
    }

    public bool ConsumeApproval(ToolApproval? approval, ToolAccessRequest request, JsonElement arguments)
    {
        if (approval is null || approval.ExpiresAt <= _timeProvider.GetUtcNow() ||
            approval.AgentId != request.AgentId || approval.ToolId != request.ToolId ||
            approval.PrincipalId != request.PrincipalId || approval.TenantId != request.TenantId ||
            approval.TaskId != request.TaskId)
            return false;

        var expected = Convert.FromHexString(HashArguments(arguments));
        byte[] actual;
        try { actual = Convert.FromHexString(approval.ArgumentsHash); }
        catch (FormatException) { return false; }

        return CryptographicOperations.FixedTimeEquals(expected, actual) &&
            _consumedApprovals.TryAdd(approval.Id, 0);
    }

    /// <summary>Hashes JSON independent of object property order; arrays retain their order.</summary>
    public static string HashArguments(JsonElement arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, arguments);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
}
