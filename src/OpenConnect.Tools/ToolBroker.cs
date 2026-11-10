using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Tools;

/// <summary>Executes tools only after fresh host authorization, schema validation, and approval checks.</summary>
public sealed class ToolBroker(ToolCatalog catalog, ToolAuthorizationService authorization)
{
    /// <summary>
    /// Executes a tool after registration, allowlist, policy, schema, and approval checks.
    /// Optional <paramref name="tryReserveToolCall"/> is consulted after approval checks and
    /// before tool invocation; a <c>false</c> result denies with <see cref="ErrorCodes.ToolNotAllowed"/>
    /// and zero side effects. Intended adapter: <c>() =&gt; ledger.TryReserveToolCall()</c>.
    /// A <c>null</c> delegate leaves invocation unenforced (existing behavior).
    /// </summary>
    public async ValueTask<JsonElement> InvokeAsync(ToolAccessRequest request, JsonElement arguments,
        ToolApproval? approval = null, Func<bool>? tryReserveToolCall = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!catalog.TryGetRegistration(request.ToolId, out var registration) ||
            registration.Descriptor.AgentId != request.AgentId)
            throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);

        // Every invocation re-checks agent allowlist membership as well as current principal/tenant/task policy.
        if (!catalog.GetForAgent(request.AgentId).Any(tool => tool.Id == request.ToolId) ||
            !await authorization.IsAllowedAsync(request, cancellationToken).ConfigureAwait(false))
            throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);

        ValidateArguments(registration.JsonSchema, arguments);
        if ((registration.RequiresApproval && approval is null) ||
            (approval is not null && !authorization.ConsumeApproval(approval, request, arguments)))
            throw new ToolAuthorizationException(ErrorCodes.ApprovalRequired);

        // Budget choke point: reservation failure denies before any tool side effect.
        if (tryReserveToolCall is not null && !tryReserveToolCall())
            throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);

        return await registration.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateArguments(string schema, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ToolValidationException("Arguments must be a JSON object.");
        using var parsed = JsonDocument.Parse(schema);
        var root = parsed.RootElement;
        var properties = root.TryGetProperty("properties", out var props) ? props : default;
        var required = root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var name in required)
            if (!arguments.TryGetProperty(name, out _)) throw new ToolValidationException($"Required argument '{name}' is missing.");
        foreach (var argument in arguments.EnumerateObject())
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(argument.Name, out var definition))
            {
                if (root.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                    throw new ToolValidationException($"Argument '{argument.Name}' is not allowed.");
                continue;
            }
            if (definition.TryGetProperty("type", out var type) && !MatchesType(type.GetString(), argument.Value))
                throw new ToolValidationException($"Argument '{argument.Name}' has an invalid type.");
        }
    }

    private static bool MatchesType(string? type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };
}

public sealed class ToolAuthorizationException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class ToolValidationException(string message) : Exception(message);
