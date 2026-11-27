using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Privacy;

namespace OpenConnect.Agents.Messaging;

/// <summary>
/// MAF <see cref="AIFunction"/> adapter that invokes a host-brokered operational messaging tool,
/// blocks any accidental secret/token/URL exposure to the model, and passes output through the
/// privacy egress gate when configured.
/// </summary>
public sealed class BrokeredMessagingAIFunction : AIFunction
{
    private static readonly HashSet<string> ForbiddenModelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessToken",
        "access_token",
        "refreshToken",
        "refresh_token",
        "bearer",
        "authorization",
        "privateKey",
        "private_key",
        "secretKey",
        "secret_key",
        "signingKey",
        "signing_key",
        "workerUrl",
        "worker_url",
        "uploadUrl",
        "upload_url",
        "downloadUrl",
        "download_url",
        "pushToken",
        "push_token"
    };

    private readonly ToolId _toolId;
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly Func<JsonElement, CancellationToken, ValueTask<JsonElement>> _invokeBroker;
    private readonly IEgressGate? _egressGate;
    private readonly IEgressContextProvider? _contextProvider;

    public BrokeredMessagingAIFunction(
        ToolId toolId,
        string name,
        string description,
        JsonElement jsonSchema,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>> invokeBroker,
        IEgressGate? egressGate = null,
        IEgressContextProvider? contextProvider = null)
    {
        if (string.IsNullOrWhiteSpace(toolId.Value))
            throw new ArgumentException("ToolId is required.", nameof(toolId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Tool description is required.", nameof(description));
        if (jsonSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool JSON schema must be an object.", nameof(jsonSchema));
        if ((egressGate is null) != (contextProvider is null))
            throw new ArgumentException("EgressGate and EgressContextProvider must either both be provided or both be null.");

        _toolId = toolId;
        _name = name;
        _description = description;
        _jsonSchema = jsonSchema.Clone();
        _invokeBroker = invokeBroker ?? throw new ArgumentNullException(nameof(invokeBroker));
        _egressGate = egressGate;
        _contextProvider = contextProvider;
    }

    public ToolId ToolId => _toolId;

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _jsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serializedArgs = SerializeArguments(arguments);
        var result = await _invokeBroker(serializedArgs, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Undefined)
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: messaging tool returned invalid JSON output.");

        if (ContainsForbiddenSecretField(result))
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: messaging tool output contained forbidden credential or transport metadata.");

        if (_egressGate is null || _contextProvider is null)
            return result;

        EgressContext context;
        try
        {
            context = _contextProvider.GetContext() with { Destination = EgressDestination.Tool };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: unable to resolve egress context for messaging tool output.");
        }

        EgressDecision decision;
        try
        {
            decision = await _egressGate.EvaluateAsync(result.GetRawText(), context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: messaging tool output blocked by privacy policy.");
        }

        if (decision is null || !decision.Allowed || decision.SanitizedContent is null ||
            decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review)
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: messaging tool output blocked by privacy policy.");
        }

        try
        {
            using var sanitizedDoc = JsonDocument.Parse(decision.SanitizedContent);
            return sanitizedDoc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: sanitized messaging tool output is not valid JSON.");
        }
    }

    private static bool ContainsForbiddenSecretField(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (ForbiddenModelKeys.Contains(prop.Name))
                        return true;
                    if (ContainsForbiddenSecretField(prop.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsForbiddenSecretField(item))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static JsonElement SerializeArguments(AIFunctionArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in arguments)
            dict[kvp.Key] = kvp.Value;
        return JsonSerializer.SerializeToElement(dict);
    }
}
